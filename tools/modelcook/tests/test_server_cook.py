# SPDX-License-Identifier: MIT
"""cook.json against a dedicated server's files (enshrouded_server.kfc, which holds no RenderModel and no material): the model steps
are skipped with a line each, the rest is cooked as on a client, and outside assets/cooked the mod is the same on both sides: the
manifest is the clients', byte for byte, and a mark the cook of 0.2.0-dev.26 put on the models' entries is taken out again.
Made-up games (tests/fakegame.py), nothing from the real one.

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
sys.path.insert(0, str(HERE.parents[2] / "schema" / "fixtures" / "dirhash"))

import dirhash  # noqa: E402  (the reference of kdh1 and of a mod's identity)
import fakegame as F  # noqa: E402
from cook import recipe as R  # noqa: E402

SKIPPED_FILES = {"cooked/a.bin", "cooked/a.blob", "cooked/b.bin", "cooked/b.blob", "cooked/c.bin", "cooked/c.blob"}


def marked_by_dev26(manifest_text):
    """assets.json as a server's cook of 0.2.0-dev.26 left it: "side": "client" on the skipped steps' entries, the file written again."""
    man = json.loads(manifest_text)
    for e in man["entries"]:
        if e["file"] in SKIPPED_FILES:
            e["side"] = "client"
    return json.dumps(man, indent=2) + "\n"


def outside_cooked(mod):
    """{path: bytes} of a mod's files that are not cook outputs."""
    mod = Path(mod)
    return {f.relative_to(mod).as_posix(): f.read_bytes() for f in sorted(mod.rglob("*"))
            if f.is_file() and f.relative_to(mod).parts[:2] != ("assets", "cooked")}


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
        mp = mod / "assets" / "assets.json"
        before, stamp = mp.read_bytes(), mp.stat().st_mtime_ns
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
        # the manifest is the shipped file, not written again: no entry marked, nothing reordered
        self.assertEqual(mp.read_bytes(), before)
        self.assertEqual(mp.stat().st_mtime_ns, stamp)
        self.assertEqual(rep["manifest"], {"missing": [], "mismatched": [], "marked": []})
        self.assertFalse(any("side" in e for e in json.loads(before)["entries"]))
        # so the server's cooked folder and the client's are one mod: only assets/cooked differs
        self.assertEqual(outside_cooked(mod), outside_cooked(self.mod_client))
        self.assertNotEqual(dirhash.kdh1(mod), dirhash.kdh1(self.mod_client))
        self.assertEqual(dirhash.mod_identity(mod), dirhash.mod_identity(self.mod_client))
        # cooked again: nothing to do, nothing changes
        self.assertFalse(R.needs_cook(r))
        blocks = mod / "blocks/c.materials.json"
        stamp_blocks = blocks.stat().st_mtime_ns
        R.run(r, self.server, self.t, update_manifest=True)
        self.assertEqual(mp.stat().st_mtime_ns, stamp)
        self.assertEqual(blocks.stat().st_mtime_ns, stamp_blocks, "a blocks file with the same bytes is not written again")

    def test_a_cook_changes_nothing_outside_cooked_in_a_mod_that_ships_what_the_cook_makes(self):
        """The author cooked once and ships assets.json and the blocks made from the glTF: neither side's cook changes the mod."""
        for name, game in (("mod-shipped-server", self.server), ("mod-shipped-client", self.client)):
            r = self._server_mod(name)
            mod = r.parent.parent
            (mod / "blocks").mkdir()
            shutil.copy(self.mod_client / "blocks/c.materials.json", mod / "blocks/c.materials.json")
            shipped = outside_cooked(mod)
            stamps = {f: (mod / f).stat().st_mtime_ns for f in shipped}
            identity = dirhash.mod_identity(mod)
            self.assertTrue(R.run(r, game, self.t, update_manifest=True)["ok"])
            self.assertEqual(outside_cooked(mod), shipped, name)
            self.assertEqual({f: (mod / f).stat().st_mtime_ns for f in shipped}, stamps, name)
            self.assertEqual(dirhash.mod_identity(mod), identity, name)

    def test_a_hand_written_manifest_is_left_as_it_is(self):
        """assets.json in the author's own formatting and order, complete: a cook has nothing to add and does not write the file again."""
        man = json.loads((self.mod_client / "assets" / "assets.json").read_text())
        man["entries"] = list(reversed(man["entries"]))
        text = json.dumps(man, indent=4, sort_keys=True) + "\n\n"
        for name, game in (("mod-hand-server", self.server), ("mod-hand-client", self.client)):
            r = self._server_mod(name)
            mp = r.parent / "assets.json"
            mp.write_text(text)
            rep = R.run(r, game, self.t, update_manifest=True)
            self.assertTrue(rep["ok"])
            self.assertEqual(mp.read_text(), text, name)

    def test_server_without_a_manifest_writes_the_clients(self):
        """A mod that ships no assets.json: the server's cook writes the file a client's cook writes, the models' entries too."""
        r = self._server_mod("mod-server-new", with_client_manifest=False)
        rep = R.run(r, self.server, self.t, update_manifest=True)
        self.assertTrue(rep["ok"])
        self.assertEqual((r.parent / "assets.json").read_bytes(), (self.mod_client / "assets" / "assets.json").read_bytes())
        self.assertEqual(dirhash.mod_identity(r.parent.parent), dirhash.mod_identity(self.mod_client))

    def test_server_without_update_manifest_says_what_is_missing_and_writes_nothing(self):
        r = self._server_mod("mod-server-plain", with_client_manifest=False)
        rep = R.run(r, self.server, self.t)
        self.assertFalse(rep["ok"])
        self.assertEqual({e["file"] for e in rep["manifest"]["missing"]}, SKIPPED_FILES | {"cooked/v.bin"})
        self.assertFalse((r.parent / "assets.json").exists())

    def test_the_marks_of_the_dev26_cook_are_taken_out(self):
        """A server folder cooked with 0.2.0-dev.26: its assets.json has the marks, its report is the old one. The cook is due again and
        puts the shipped file back, byte for byte."""
        shipped = (self.mod_client / "assets" / "assets.json").read_text()
        r = self._server_mod("mod-server-dev26")
        mod, mp = r.parent.parent, r.parent / "assets.json"
        R.run(r, self.server, self.t, update_manifest=True)
        self.assertFalse(R.needs_cook(r))
        # as dev.26 left it: the marked manifest, and a report without the "marked" list
        mp.write_text(marked_by_dev26(shipped))
        rp = r.parent / "cooked" / "cook-report.json"
        old = json.loads(rp.read_text())
        old["manifest"] = {"missing": [], "mismatched": []}
        rp.write_text(json.dumps(old, indent=2) + "\n")
        self.assertNotEqual(dirhash.mod_identity(mod), dirhash.mod_identity(self.mod_client), "what dev.26 left: another mod than the players'")
        self.assertTrue(R.needs_cook(r), "an old server report means: cook again")
        # without update_manifest the cook only says so
        rep = R.run(r, self.server, self.t)
        self.assertTrue(rep["ok"])
        self.assertEqual(set(rep["manifest"]["marked"]), SKIPPED_FILES)
        self.assertEqual(mp.read_text(), marked_by_dev26(shipped))
        rep = R.run(r, self.server, self.t, update_manifest=True)
        self.assertTrue(rep["ok"])
        self.assertEqual(set(rep["manifest"]["unmarked"]), SKIPPED_FILES)
        self.assertEqual(rep["manifest"]["marked"], [])
        self.assertEqual(mp.read_text(), shipped)
        self.assertEqual(dirhash.mod_identity(mod), dirhash.mod_identity(self.mod_client))
        self.assertFalse(R.needs_cook(r))

    def test_the_marks_are_taken_out_in_place_and_on_a_client_too(self):
        """A marked manifest in the author's own order (the mark is all the old cook changed): the order stays. A player who got the
        server's marked file (a modpack built by 0.2.0-dev.26) has the shipped one again after a cook."""
        man = json.loads((self.mod_client / "assets" / "assets.json").read_text())
        man["entries"] = list(reversed(man["entries"]))
        shipped = json.dumps(man, indent=2) + "\n"
        for name, game in (("mod-marked-server", self.server), ("mod-marked-client", self.client)):
            r = self._server_mod(name)
            mp = r.parent / "assets.json"
            mp.write_text(marked_by_dev26(shipped))
            rep = R.run(r, game, self.t, update_manifest=True)
            self.assertTrue(rep["ok"], name)
            self.assertEqual(set(rep["manifest"]["unmarked"]), SKIPPED_FILES, name)
            self.assertEqual(mp.read_text(), shipped, name)

    def test_a_mark_on_an_entry_that_differs_otherwise_is_a_mismatch(self):
        r = self._server_mod("mod-server-mismatch")
        mp = r.parent / "assets.json"
        man = json.loads(mp.read_text())
        e = next(x for x in man["entries"] if x["file"] == "cooked/b.bin")
        e["side"], e["mode"] = "client", "override"
        mp.write_text(json.dumps(man, indent=2) + "\n")
        rep = R.run(r, self.server, self.t)
        self.assertFalse(rep["ok"])
        self.assertEqual([x["file"] for x in rep["manifest"]["mismatched"]], ["cooked/b.bin"])
        self.assertEqual(rep["manifest"]["marked"], [])
        self.assertTrue(R.run(r, self.server, self.t, update_manifest=True)["ok"])
        after = {x["file"]: x for x in json.loads(mp.read_text())["entries"]}
        self.assertEqual(after["cooked/b.bin"], next(x for x in json.loads((self.mod_client / "assets" / "assets.json").read_text())["entries"] if x["file"] == "cooked/b.bin"))

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


class OutFolder(unittest.TestCase):
    """A recipe's "out" is the mod's assets/cooked folder or a folder in it: a cook writes nowhere else."""

    def _recipe(self, root, out, where="assets"):
        mod = Path(root) / "mod"
        (mod / where).mkdir(parents=True, exist_ok=True)
        (mod / "kiln.mod.json").write_text(json.dumps({"id": "out-mod", "version": "1.0.0"}))
        r = mod / where / "cook.json"
        doc = {"format": 1, "tool": "modelcook", "steps": []}
        if out is not None:
            doc["out"] = out
        r.write_text(json.dumps(doc))
        return r

    def test_inside_assets_cooked(self):
        with tempfile.TemporaryDirectory() as d:
            for out, where in ((None, "assets"), ("cooked", "assets"), ("cooked/models", "assets"), ("./cooked/", "assets"),
                               ("../assets/cooked/impact", "impact"), ("../assets/cooked", "impact")):
                r = self._recipe(d, out, where)
                doc = R.load(r)
                self.assertTrue(str(R.out_dir(r, doc)).startswith(str((Path(d) / "mod/assets/cooked").resolve())), out)

    def test_outside_is_refused_with_what_to_change(self):
        with tempfile.TemporaryDirectory() as d:
            for out, where in (("built", "assets"), (".", "assets"), ("../cooked", "assets"), ("cooked/../src", "assets"), ("cooked-more", "assets"),
                               ("Cooked", "assets"), (str(Path(d) / "elsewhere"), "assets"), ("cooked", "impact"), ("", "assets")):
                r = self._recipe(d, out, where)
                R.load(r)  # the reader takes the recipe's shape wherever the file lies (kiln lint's reader does the same)
                for cook in (lambda: R.needs_cook(r), lambda: R.run(r, None, None, update_manifest=True)):
                    with self.assertRaises(R.RecipeError, msg=out) as e:
                        cook()
                    if out:
                        self.assertIn("outside the mod's assets/cooked folder", str(e.exception), out)
                        self.assertIn('"out": "cooked"', str(e.exception))

    def test_the_command_refuses_and_writes_nothing(self):
        import subprocess
        with tempfile.TemporaryDirectory() as d:
            r = self._recipe(d, "built")
            before = sorted(str(f) for f in Path(d).rglob("*"))
            p = subprocess.run([sys.executable, str(HERE.parent / "modelcook.py"), "--game", d, "recipe", str(r), "--update-manifest"],
                               capture_output=True, text=True, timeout=120)
            self.assertEqual(p.returncode, 2, p.stdout + p.stderr)
            self.assertIn("outside the mod's assets/cooked folder", p.stderr)
            self.assertEqual(sorted(str(f) for f in Path(d).rglob("*")), before)


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
