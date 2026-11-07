# SPDX-License-Identifier: MIT
"""Sound blocks -> recipe steps (sndcook/blocks.py). Offline, no game files.

    python3 -m unittest discover -s tools/soundcook/tests -p 'test_blocks.py' -v
"""
import json
import sys
import tempfile
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
        with tempfile.TemporaryDirectory() as t:
            d = mod(t, {"click": {"clips": ["audio/a.wav"]}}, {"format": 1, "tool": "soundcook", "guidNamespace": str(uuid.uuid4()),
                                                              "steps": [{"op": "cue", "name": "click", "sounds": []}]})
            with self.assertRaisesRegex(SB.BlocksError, "already has a cue step 'click'"):
                SB.write(d)


if __name__ == "__main__":
    unittest.main()
