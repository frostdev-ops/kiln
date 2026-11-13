# SPDX-License-Identifier: MIT
"""Sound blocks -> recipe steps (sndcook/blocks.py). Offline, no game files.

    python3 -m unittest discover -s tools/soundcook/tests -p 'test_blocks.py' -v
"""
import json
import sys
import tempfile
import subprocess
import unittest
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from sndcook import blocks as SB  # noqa: E402


def mod(tmp, blocks, recipe=None):
    d = Path(tmp)
    (d / "blocks").mkdir()
    (d / "audio").mkdir()
    for f in ("a.wav", "b.ogg"):
        (d / "audio" / f).write_bytes(b"RIFF")
    (d / "kiln.mod.json").write_text('{ "id": "my-mod", // comment\n }')
    (d / "blocks" / "s.json").write_text(json.dumps({"format": 1, "sounds": blocks}))
    if recipe is not None:
        (d / "assets").mkdir()
        (d / "assets" / "cook.json").write_text(json.dumps(recipe))
    return d


class Blocks(unittest.TestCase):
    def test_new_recipe(self):
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"hum": {"clips": ["audio/a.wav", "audio/b.ogg"], "range": [3, 40], "volume": 0.5, "loop": True}})
            changed, path, doc, names = SB.write(d)
            self.assertTrue(changed)
            self.assertEqual(names, ["hum"])
            self.assertEqual(doc["tool"], "soundcook")
            # the namespace Kiln.Blocks derives for a mod with no recipe of its own
            self.assertEqual(doc["guidNamespace"], str(uuid.uuid5(uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77"), "my-mod:sounds")))
            steps = doc["steps"]
            self.assertEqual([(s["op"], s["name"]) for s in steps], [("sound", "hum.1"), ("sound", "hum.2"), ("cue", "hum")])
            self.assertEqual(steps[0]["src"], "../audio/a.wav")
            self.assertEqual(steps[0]["channels"], "mono")
            self.assertEqual(steps[2]["positional"], {"min": 3, "max": 40})
            self.assertEqual(steps[2]["sounds"], ["hum.1", "hum.2"])
            self.assertTrue(steps[2]["loop"])
            self.assertEqual(json.loads(path.read_text()), doc)
            self.assertFalse(SB.write(d)[0], "a second run changes nothing")

    def test_existing_recipe_keeps_its_steps_and_namespace(self):
        with tempfile.TemporaryDirectory() as t:
            ns = str(uuid.uuid4())
            own = {"op": "sound", "name": "click", "src": "../audio/a.wav", "tool": "soundcook"}
            d = mod(t, {"hum": {"clips": ["audio/a.wav"]}}, {"format": 1, "tool": "modelcook", "out": "cooked", "guidNamespace": ns, "steps": [own]})
            _, _, doc, _ = SB.write(d)
            self.assertEqual(doc["guidNamespace"], ns)
            self.assertEqual(doc["steps"][0], own)
            self.assertTrue(all(s["tool"] == "soundcook" for s in doc["steps"][1:]), "a modelcook recipe names soundcook on each step")
            # the block goes away: so do its steps, and only those
            (d / "blocks" / "s.json").write_text(json.dumps({"format": 1, "sounds": {}}))
            _, _, doc, _ = SB.write(d)
            self.assertEqual(doc["steps"], [own])
            self.assertNotIn("blockSounds", doc)

    def test_errors(self):
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"bad": {"clips": ["../outside.wav"]}})
            with self.assertRaisesRegex(SB.BlocksError, r"sounds.bad.clips\[0\]"):
                SB.write(d)

    def test_check_detects_namespace_only_changes_without_writing(self):
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"hum": {"clips": ["audio/a.wav"]}})
            _, path, existing, _ = SB.write(d)
            del existing["guidNamespace"]
            path.write_text(json.dumps(existing))
            before = path.read_bytes()
            changed, _, doc, _ = SB.write(d, check=True)
            self.assertTrue(changed)
            self.assertIn("guidNamespace", doc)
            self.assertEqual(existing["steps"], doc["steps"])
            self.assertEqual(before, path.read_bytes())
            SB.write(d)
            self.assertFalse(SB.write(d, check=True)[0])

    def test_owned_steps_do_not_delete_a_different_op_with_the_same_name(self):
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"hum": {"clips": ["audio/a.wav"]}})
            _, path, doc, _ = SB.write(d)
            own = {"op": "sound", "name": "hum", "src": "../audio/a.wav"}
            doc["steps"].append(own)
            path.write_text(json.dumps(doc))
            self.assertIn(own, SB.write(d)[2]["steps"])

    def test_legacy_ownership_records_preserve_other_operations_too(self):
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"hum": {"clips": ["audio/a.wav"]}})
            _, path, doc, _ = SB.write(d)
            doc["blockSounds"] = ["hum.1", "hum"]
            own = [{"op": "sound", "name": "hum", "src": "../audio/a.wav"}, {"op": "cue", "name": "hum.1", "sounds": ["hum"]}]
            doc["steps"] += own
            path.write_text(json.dumps(doc))
            updated = SB.write(d)[2]
            self.assertTrue(all(st in updated["steps"] for st in own))
            self.assertEqual(updated["blockSounds"], ["sound:hum.1", "cue:hum"])

    def test_deleting_the_last_block_invalidates_the_recipe_check(self):
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"hum": {"clips": ["audio/a.wav"]}})
            SB.write(d)
            (d / "blocks/s.json").unlink()
            script = Path(__file__).resolve().parents[1] / "soundcook.py"
            r = subprocess.run([sys.executable, str(script), "blocks", str(d), "--check"], capture_output=True, text=True)
            self.assertEqual(r.returncode, 1, r.stderr)
            self.assertIn("behind the blocks", r.stdout)
            SB.write(d)
            self.assertFalse(SB.write(d, check=True)[0])

    def test_invalid_ranges_and_missing_files_are_reported_before_writing(self):
        for b in ({"clips": ["audio/missing.wav"]}, {"clips": ["audio/a.wav"], "range": [4, 2]},
                  {"clips": ["audio/a.wav"], "volume": float("nan")}):
            with tempfile.TemporaryDirectory() as t:
                d = mod(t, {"hum": b})
                with self.assertRaises(SB.BlocksError):
                    SB.write(d)
                self.assertFalse((d / "assets/cook.json").exists())

    def test_build_supplied_mod_id_is_used_before_a_manifest_exists(self):
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"hum": {"clips": ["audio/a.wav"]}})
            (d / "kiln.mod.json").unlink()
            _, _, doc, _ = SB.write(d, mod="the-sdk-id")
            self.assertEqual(doc["guidNamespace"], SB.derived_namespace("the-sdk-id"))

    def test_recipe_collisions_are_reported(self):
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"click": {"clips": ["audio/a.wav"]}}, {"format": 1, "tool": "soundcook", "guidNamespace": str(uuid.uuid4()),
                                                              "steps": [{"op": "cue", "name": "click", "sounds": []}]})
            with self.assertRaisesRegex(SB.BlocksError, "already has a cue step 'click'"):
                SB.write(d)

    def test_the_recipe_is_replaced_whole_never_cut_or_written_through_a_link(self):
        """The recipe is written to a temporary file renamed over it: a cook stopped halfway (or a launcher that died) leaves
        the old recipe or the new one, never a cut one; a recipe that is a link is refused, and what it points at stays."""
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"hum": {"clips": ["audio/a.wav"]}}, recipe={"format": 1, "tool": "modelcook", "steps": []})
            path = d / "assets" / "cook.json"
            before = path.stat().st_ino
            SB.write(d)
            self.assertNotEqual(path.stat().st_ino, before, "a new file took the recipe's name")
            self.assertEqual([p.name for p in path.parent.iterdir() if p.name.endswith(".cook-tmp")], [])
            # a stop in the middle of the write: the recipe on disk is still the whole old one
            (d / "blocks" / "s.json").write_text(json.dumps({"format": 1, "sounds": {"boom": {"clips": ["audio/b.ogg"]}}}))
            old = path.read_bytes()
            real = SB.CP.os.replace
            def stopped(*a):
                raise KeyboardInterrupt
            SB.CP.os.replace = stopped
            try:
                with self.assertRaises(KeyboardInterrupt):
                    SB.write(d)
            finally:
                SB.CP.os.replace = real
            self.assertEqual(path.read_bytes(), old)
            self.assertEqual([p.name for p in path.parent.iterdir() if p.name.endswith(".cook-tmp")], [])
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"hum": {"clips": ["audio/a.wav"]}})
            (d / "assets").mkdir()
            theirs = Path(t) / "theirs.json"
            theirs.write_text('{"format": 1, "steps": []}')
            try:
                (d / "assets" / "cook.json").symlink_to(theirs)
            except (OSError, NotImplementedError):
                self.skipTest("no symbolic links here")
            with self.assertRaises(SB.BlocksError):
                SB.write(d)
            self.assertEqual(theirs.read_text(), '{"format": 1, "steps": []}')
        # nor through an assets folder that is a link: the recipe behind it is somebody else's
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"hum": {"clips": ["audio/a.wav"]}})
            elsewhere = Path(t) / "elsewhere"
            elsewhere.mkdir()
            (elsewhere / "cook.json").write_text('{"format": 1, "steps": []}')
            (d / "assets").symlink_to(elsewhere, target_is_directory=True)
            with self.assertRaisesRegex(SB.BlocksError, "is a link"):
                SB.write(d)
            self.assertEqual((elsewhere / "cook.json").read_text(), '{"format": 1, "steps": []}')
            self.assertEqual([p.name for p in elsewhere.iterdir()], ["cook.json"])


if __name__ == "__main__":
    unittest.main()
