# SPDX-License-Identifier: MIT
"""cook.json against a dedicated server's files (enshrouded_server.kfc, which holds no RenderModel and no material): the model steps
are skipped with a line each, the rest is cooked as on a client, and outside assets/cooked the mod is the same on both sides: the
manifest is the clients', byte for byte, and a mark the cook of 0.2.0-dev.26 put on the models' entries is taken out again.
Made-up games (tests/fakegame.py), nothing from the real one.

    python3 -m unittest discover -s tools/modelcook/tests -p test_server_cook.py -v
"""
import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

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

    def _legacy_report(self, r):
        """The report a server's cook of 0.2.0-dev.26 left: this cook's, without the lists this version adds."""
        R.run(r, self.server, self.t, update_manifest=True)
        rp = r.parent / "cooked" / "cook-report.json"
        old = json.loads(rp.read_text())
        old["manifest"] = {"missing": [], "mismatched": []}
        rp.write_text(json.dumps(old, indent=2) + "\n")

    def test_the_marks_are_taken_out_in_place(self):
        """A marked manifest in the author's own order (the mark is all the old cook changed), with that cook's report: the marks go, the
        order stays."""
        man = json.loads((self.mod_client / "assets" / "assets.json").read_text())
        man["entries"] = list(reversed(man["entries"]))
        shipped = json.dumps(man, indent=2) + "\n"
        r = self._server_mod("mod-marked-server")
        mp = r.parent / "assets.json"
        mp.write_text(shipped)
        self._legacy_report(r)
        mp.write_text(marked_by_dev26(shipped))
        rep = R.run(r, self.server, self.t, update_manifest=True)
        self.assertTrue(rep["ok"])
        self.assertEqual(set(rep["manifest"]["unmarked"]), SKIPPED_FILES)
        self.assertNotIn("legacyMarks", rep["manifest"])
        self.assertEqual(mp.read_text(), shipped)

    def test_a_mark_the_mods_author_wrote_stays(self):
        """Model entries marked "side": "client" and no report of a 0.2.0-dev.26 server cook: nothing says a cook wrote the mark, so it is
        the mod's. The file is not written, on a server or a client, and the cook is not due again because of it."""
        authored = marked_by_dev26((self.mod_client / "assets" / "assets.json").read_text())
        for name, game in (("mod-authored-server", self.server), ("mod-authored-client", self.client)):
            r = self._server_mod(name)
            mp = r.parent / "assets.json"
            mp.write_text(authored)
            stamp = mp.stat().st_mtime_ns
            for _ in range(2):  # the second cook has the first one's report: still no evidence
                rep = R.run(r, game, self.t, update_manifest=True)
                self.assertTrue(rep["ok"], name)
                self.assertEqual(set(rep["manifest"]["marked"]), SKIPPED_FILES, name)
                self.assertNotIn("unmarked", rep["manifest"])
                self.assertNotIn("legacyMarks", rep["manifest"])
                self.assertEqual((mp.read_text(), mp.stat().st_mtime_ns), (authored, stamp), name)
                self.assertFalse(R.needs_cook(r), name)
        # a client's report is no evidence either, even without the lists this version writes
        r = self._server_mod("mod-authored-old-client")
        mp = r.parent / "assets.json"
        R.run(r, self.client, self.t, update_manifest=True)
        rp = r.parent / "cooked" / "cook-report.json"
        old = json.loads(rp.read_text())
        old["manifest"] = {"missing": [], "mismatched": []}
        rp.write_text(json.dumps(old, indent=2) + "\n")
        mp.write_text(authored)
        self.assertEqual(R.run(r, self.client, self.t, update_manifest=True)["manifest"]["marked"], sorted(SKIPPED_FILES, key=[e["file"] for e in json.loads(authored)["entries"]].index))
        self.assertEqual(mp.read_text(), authored)

    def test_the_evidence_survives_a_cook_that_may_not_write_the_manifest(self):
        """An old server report, then a cook without update_manifest (it writes a new report): the next cook still knows the marks are a cook's."""
        shipped = (self.mod_client / "assets" / "assets.json").read_text()
        r = self._server_mod("mod-marked-twice")
        mp = r.parent / "assets.json"
        self._legacy_report(r)
        mp.write_text(marked_by_dev26(shipped))
        rep = R.run(r, self.server, self.t)
        self.assertEqual((set(rep["manifest"]["marked"]), rep["manifest"].get("legacyMarks")), (SKIPPED_FILES, True))
        self.assertTrue(R.needs_cook(r))
        rep = R.run(r, self.server, self.t, update_manifest=True)
        self.assertEqual(set(rep["manifest"]["unmarked"]), SKIPPED_FILES)
        self.assertEqual(mp.read_text(), shipped)
        self.assertFalse(R.needs_cook(r))

    def test_a_stem_cannot_lead_out_of_cooked(self):
        """A step's stem with ".." in it would put its files (and the blocks made from its glTF) elsewhere in the mod, where they count as the
        mod: refused before anything is written, on both sides (a server names the files in the manifest without writing them)."""
        for stem in ("../../escaped", "../escaped", "sub/../../escaped"):
            for side, game in (("server", self.server), ("client", self.client)):
                rec = json.loads(json.dumps(self.recipe))
                rec["steps"][1]["stem"] = stem
                r = F.write_mod(self.root / f"mod-escape-{side}", rec)
                mod = r.parent.parent
                before = sorted(str(f) for f in mod.rglob("*"))
                with self.assertRaises(R.RecipeError, msg=f"{stem} {side}") as e:
                    R.run(r, game, self.t, update_manifest=True)
                self.assertIn("outside the mod's assets/cooked folder", str(e.exception))
                self.assertIn("plain name or stem", str(e.exception))
                self.assertEqual(sorted(str(f) for f in mod.rglob("*")), before, f"{stem} {side}: nothing written")
        # the blocks file of a "materials": "gltf" step is named after the stem too
        rec = json.loads(json.dumps(self.recipe))
        rec["steps"][-1]["stem"] = "../c"
        r = F.write_mod(self.root / "mod-escape-blocks", rec)
        with self.assertRaises(R.RecipeError):
            R.run(r, self.client, self.t, update_manifest=True)
        self.assertFalse((r.parent.parent / "c.materials.json").exists())

    def test_an_authored_mark_stays_when_another_entry_is_repaired(self):
        """The author's "side": "client" marks on the models' entries, and another entry of the recipe missing from the file: the cook with
        --update-manifest writes the missing one and leaves the marked ones as the author has them."""
        authored = json.loads(marked_by_dev26((self.mod_client / "assets" / "assets.json").read_text()))
        dropped = next(e for e in authored["entries"] if e["file"] == "cooked/v.bin")
        authored["entries"].remove(dropped)
        r = self._server_mod("mod-authored-repair")
        mp = r.parent / "assets.json"
        mp.write_text(json.dumps(authored, indent=2) + "\n")
        rep = R.run(r, self.client, self.t, update_manifest=True)
        self.assertTrue(rep["ok"])
        after = {e["file"]: e for e in json.loads(mp.read_text())["entries"]}
        self.assertEqual(after["cooked/v.bin"], dropped, "the missing entry is written")
        for f in SKIPPED_FILES:
            self.assertEqual(after[f].get("side"), "client", f"{f}: the author's mark stays")
        self.assertFalse(R.needs_cook(r))

    def test_a_link_in_the_reports_place_or_a_dotdot_after_a_link_is_refused_before_anything_is_written(self):
        """The report is a file of the cook like its outputs: a link in its place is refused, as is a stem that climbs out of a link in
        assets/cooked ("link/../escape" normalises to a name in assets/cooked, while the system resolves it from where the link leads)."""
        outside = self.root / "outside-report"
        (outside / "deep").mkdir(parents=True, exist_ok=True)
        victim = outside / "victim.json"
        victim.write_text("theirs")
        for side, game in (("client", self.client), ("server", self.server)):
            r = self._server_mod(f"mod-report-link-{side}")
            cooked = r.parent / "cooked"
            cooked.mkdir(exist_ok=True)
            (cooked / "cook-report.json").symlink_to(victim)
            before = sorted(str(f) for f in r.parent.parent.rglob("*"))
            with self.assertRaises(R.RecipeError, msg=side) as e:
                R.run(r, game, self.t, update_manifest=True)
            self.assertIn("is a link", str(e.exception))
            self.assertEqual(victim.read_text(), "theirs")
            self.assertEqual(sorted(str(f) for f in r.parent.parent.rglob("*")), before, f"{side}: nothing written")
            (cooked / "cook-report.json").unlink()
            (cooked / "link").symlink_to(outside / "deep", target_is_directory=True)
            rec = json.loads(json.dumps(self.recipe))
            rec["steps"][1]["stem"] = "link/../escape"
            r = F.write_mod(r.parent.parent, rec)
            with self.assertRaises(R.RecipeError, msg=side) as e:
                R.run(r, game, self.t, update_manifest=True)
            self.assertIn("climbs out of the link", str(e.exception))
            self.assertEqual(sorted(f.name for f in outside.iterdir()), ["deep", "victim.json"])
            self.assertFalse((cooked / "escape.bin").exists())

    @mock.patch.dict(os.environ, {"KILN_COOK_RECEIPT": "1"})
    def test_a_cook_records_what_it_wrote_in_the_game_it_read(self):
        """The receipt (cookpaths.receipt): in <game>/kiln/state/cook-receipts, one file per mod folder and recipe, listing every file the
        cook wrote into assets/cooked (its report too) with its SHA-256; the next cook of the recipe replaces it. A game folder without Kiln
        gets none, and nothing is written there."""
        import hashlib
        # a tool run by hand writes nothing into the game's folder: only a cook Kiln runs asks for the receipt
        gdir = Path(self.client.path).parent
        (gdir / "kiln").mkdir(exist_ok=True)
        with mock.patch.dict(os.environ, {"KILN_COOK_RECEIPT": ""}):
            R.run(self._server_mod("mod-receipt-by-hand"), self.client, self.t, update_manifest=True)
        self.assertFalse((gdir / "kiln" / "state").exists())
        shutil.rmtree(gdir / "kiln")
        for side, game in (("client", self.client), ("server", self.server)):
            gdir = Path(game.path).parent
            receipts = gdir / "kiln" / "state" / "cook-receipts"
            shutil.rmtree(gdir / "kiln", ignore_errors=True)
            r = self._server_mod(f"mod-receipt-{side}")
            R.run(r, game, self.t, update_manifest=True)
            self.assertFalse((gdir / "kiln").exists(), f"{side}: no Kiln there, no receipt and no folder made")
            (gdir / "kiln").mkdir()
            rep = R.run(r, game, self.t, update_manifest=True)
            files = list(receipts.iterdir())
            self.assertEqual(len(files), 1, files)
            doc = json.loads(files[0].read_text())
            self.assertEqual((doc["format"], doc["tool"], doc["recipe"], Path(doc["mod"])), (1, "modelcook", "assets/cook.json", r.parent.parent))
            cooked = r.parent / "cooked"
            on_disk = {f.relative_to(r.parent).as_posix(): hashlib.sha256(f.read_bytes()).hexdigest() for f in cooked.rglob("*") if f.is_file()}
            self.assertEqual(doc["files"], on_disk, side)
            self.assertIn("cooked/cook-report.json", doc["files"])
            self.assertEqual({o["file"] for o in rep["outputs"]} | {"cooked/cook-report.json"}, set(doc["files"]))
            R.run(r, game, self.t, update_manifest=True)
            self.assertEqual(list(receipts.iterdir()), files, "the next cook of the recipe replaces its receipt")
            shutil.rmtree(gdir / "kiln")

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

    def test_the_four_tools_share_one_check(self):
        tools = HERE.parents[1]
        texts = {d: (tools / d / "cookpaths.py").read_text() for d in ("modelcook/cook", "animcook/anicook", "soundcook/sndcook", "impact/impact")}
        self.assertEqual(len(set(texts.values())), 1, "cookpaths.py differs between the cook tools: " + ", ".join(texts))

    def test_spelling_and_links(self):
        from cook import cookpaths as CP
        with tempfile.TemporaryDirectory() as d:
            r = self._recipe(d, None)
            mod = r.parent.parent
            ok = CP.cooked(r, "cooked/a.bin", "x")
            self.assertEqual(ok, mod / "assets/cooked/a.bin")
            for target in ("cooked/../x.bin", "cooked/sub/../../x.bin", "../x.bin", "Cooked/a.bin", "cooked", "../../outside/assets/cooked/a.bin", str(Path(d) / "x.bin")):
                if target == "cooked":
                    CP.cooked(r, target, "x")  # the folder itself
                    continue
                with self.assertRaises(CP.Refused, msg=target):
                    CP.cooked(r, target, "x")
            # where the folder is there under another spelling, the refusal says so
            (mod / "assets" / "Cooked").mkdir()
            with self.assertRaises(CP.Refused) as e:
                CP.cooked(r, "Cooked/a.bin", "x")
            self.assertIn("it has to be assets/cooked, in lower case", str(e.exception))
            # a link named assets/cooked, a link inside it, a link in place of an output: all refused, nothing is written through one
            outside = Path(d) / "outside"
            outside.mkdir()
            (mod / "assets" / "cooked").symlink_to(outside, target_is_directory=True)
            with self.assertRaises(CP.Refused) as e:
                CP.out_dir(r, "cooked", "cooked")
            self.assertIn("is a link", str(e.exception))
            (mod / "assets" / "cooked").unlink()
            (mod / "assets" / "cooked").mkdir()
            (mod / "assets" / "cooked" / "sub").symlink_to(outside, target_is_directory=True)
            with self.assertRaises(CP.Refused):
                CP.cooked(r, "cooked/sub/a.bin", "x")
            (mod / "assets" / "cooked" / "a.bin").symlink_to(outside / "a.bin")
            with self.assertRaises(CP.Refused):
                CP.cooked(r, "cooked/a.bin", "x")
            self.assertEqual(list(outside.iterdir()), [])
            # the manifest and the blocks file: their exact place only
            self.assertEqual(CP.manifest(r, "assets.json"), mod / "assets/assets.json")
            self.assertEqual(CP.blocks_file(r, mod / "blocks/a.materials.json"), mod / "blocks/a.materials.json")
            for bad in ("../assets.json", "cooked/assets.json", "other.json"):
                with self.assertRaises(CP.Refused, msg=bad):
                    CP.manifest(r, bad)
            for bad in ("blocks/../a.materials.json", "blocks/sub/a.materials.json", "blocks/a.json", "a.materials.json"):
                with self.assertRaises(CP.Refused, msg=bad):
                    CP.blocks_file(r, mod / bad)
            # a mod reached through a link above its folder is fine: that link is the player's
            (Path(d) / "via").symlink_to(mod, target_is_directory=True)
            self.assertEqual(CP.cooked(Path(d) / "via" / "assets" / "cook.json", "cooked/b.bin", "x"), Path(d) / "via/assets/cooked/b.bin")

    def test_a_dotdot_climbs_out_of_no_link_and_write_replaces_a_link(self):
        from cook import cookpaths as CP
        with tempfile.TemporaryDirectory() as d:
            r = self._recipe(d, None)
            mod = r.parent.parent
            outside = Path(d) / "outside"
            (outside / "deep").mkdir(parents=True)
            (mod / "assets" / "cooked" / "sub").mkdir(parents=True)
            self.assertEqual(CP.cooked(r, "cooked/sub/../a.bin", "x"), mod / "assets/cooked/a.bin", "a '..' after a folder is fine")
            (mod / "assets" / "cooked" / "link").symlink_to(outside / "deep", target_is_directory=True)
            for target in ("cooked/link/../a.bin", str(mod / "assets/cooked/link/../a.bin"), "cooked/link/./../a.bin"):
                with self.assertRaises(CP.Refused, msg=target) as e:
                    CP.cooked(r, target, "x")
                self.assertIn("climbs out of the link", str(e.exception))
            with self.assertRaises(CP.Refused):
                CP.manifest(r, "cooked/link/../../assets.json")
            # write() goes to the path it is given through a file beside it: a link put in an output's place after the check is
            # replaced, the file it pointed to is left as it was
            target = CP.cooked(r, "cooked/a.bin", "x")
            (outside / "theirs.bin").write_bytes(b"theirs")
            target.symlink_to(outside / "theirs.bin")
            with self.assertRaises(CP.Refused):
                CP.write(target, b"cooked")
            target.unlink()
            self.assertEqual(CP.write(target, b"cooked"), target)
            self.assertEqual((target.read_bytes(), (outside / "theirs.bin").read_bytes()), (b"cooked", b"theirs"))
            self.assertEqual(sorted(f.name for f in target.parent.iterdir()), ["a.bin", "link", "sub"], "no temporary file is left")

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
            (root / "server" / "kiln").mkdir()
            r = F.write_mod(root / "mod")
            env = dict(os.environ, KILN_OUT=str(root / "out"), TMPDIR=d, KILN_COOK_RECEIPT="1")
            p = subprocess.run([sys.executable, str(HERE.parent / "modelcook.py"), "--game", str(root / "server"), "recipe", str(r), "--update-manifest"],
                               capture_output=True, text=True, env=env, timeout=300)
            self.assertEqual(p.returncode, 0, p.stdout + p.stderr)
            self.assertIn(f"step 0 model a: {R.SKIPPED}", p.stdout)
            self.assertIn(f"step 1 model b: {R.SKIPPED}", p.stdout)
            self.assertIn("clip idle:", p.stdout)
            self.assertTrue((root / "mod/assets/cooked/clip_idle.bin").exists())
            self.assertTrue((root / "mod/assets/cooked/v.bin").exists())
            # each tool of the recipe recorded what it wrote, in the server's receipts: modelcook and the animcook it handed its steps to
            receipts = {json.loads(f.read_text())["tool"]: json.loads(f.read_text())["files"] for f in (root / "server/kiln/state/cook-receipts").iterdir()}
            self.assertEqual(sorted(receipts), ["animcook", "modelcook"])
            self.assertIn("cooked/clip_idle.bin", receipts["animcook"])
            self.assertIn("cooked/v.bin", receipts["modelcook"])


if __name__ == "__main__":
    unittest.main()
