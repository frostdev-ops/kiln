# SPDX-License-Identifier: MIT
"""The cook steps a mod's blocks/*.json asks for (anicook/blocks.py), with no game files: the sample golem in a temp mod, its animation and body as
blocks, the steps the recipe gains and the graph they cook to. `python3 -m unittest discover -s tools/animcook/tests -v`."""
import json
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path[:0] = [str(ROOT), str(ROOT / "samples"), str(ROOT.parent / "kfc"), str(ROOT.parent / "modelcook")]

import make_golem  # noqa: E402
from anicook import blocks as B, graphgen as G, recipe as R  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

TYPES = TypeDb.load(ROOT / "types" / "anim-types.json")

ANIM = {"clips": "models/golem.glb", "idle": "Idle", "walk": {"blend": {"input": "moveDirection", "clips": {"0": "WalkF", "-180": "WalkB"}}},
        "run": "Run", "dead": {"clip": "Fall"}, "hit": {"clip": "Flinch", "reference": "Ref", "bones": ["spine", "head"]},
        "states": {"slam": {"clip": "Slam", "speed": 1.2, "then": "rest"}, "rest": "Step"}}
BODY = {"model": "models/golem.glb", "slots": {"handR": "hand_r"}}


class Blocks(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.mod = Path(self.tmp.name)
        (self.mod / "models").mkdir()
        (self.mod / "blocks").mkdir()
        (self.mod / "assets").mkdir()
        make_golem.write(self.mod / "models" / "golem.glb")
        (self.mod / "kiln.mod.json").write_text(json.dumps({"id": "golem-mod"}))
        (self.mod / "assets" / "cook.json").write_text(json.dumps({"format": 1, "tool": "animcook", "steps": []}))

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, doc, name="golem.json"):
        (self.mod / "blocks" / name).write_text("// a comment, as the block loader allows\n" + json.dumps(doc, indent=1).replace("\n}", ",\n}"))

    def test_steps(self):
        self.write({"format": 1, "animations": {"golem-moves": ANIM}, "bodies": {"golem-body": BODY}})
        steps = B.steps_from_blocks(self.mod, self.mod / "assets")
        ops = [(s["tool"], s["op"], s.get("name")) for s in steps]
        self.assertIn(("animcook", "skeleton", "golem"), ops)
        self.assertIn(("animcook", "graph", "golem-moves"), ops)
        self.assertIn(("animcook", "graph", "golem-moves.golem-body"), ops, "a body with slots gets a graph of its own")
        self.assertIn(("modelcook", "model", "models/golem"), ops)
        graph = next(s for s in steps if s.get("name") == "golem-moves.golem-body")
        self.assertEqual(graph["slots"], {"HandR": "hand_r"}, "slots as the graph names them, however JSON cased them")
        self.assertEqual(set(graph["states"]), {"Idle", "Walk", "Run", "Dead", "slam", "rest", "IdleCombat", "Fall", "Stagger", "Stun"},
                         "IdleCombat and Fall stand as in Idle; Stagger and Stun play the hit clip")
        self.assertEqual(graph["states"]["IdleCombat"]["clip"], graph["states"]["Idle"]["clip"])
        self.assertEqual(graph["states"]["Stagger"]["clip"], "golem.Flinch.once", "a hit reaction plays the hit clip over the whole body")
        self.assertEqual(graph["states"]["Stun"]["engine"], "Stun")
        clips = next(s for s in steps if s["op"] == "clips")["clips"]
        self.assertTrue(clips["golem.Idle"]["loop"] and not clips["golem.Slam.once"]["loop"], "the engine's moving states loop, your own play once")
        self.assertEqual(steps[0]["gltf"], "../models/golem.glb", "paths are the recipe's")

    def test_reaction_defaults(self):
        self.write({"format": 1, "animations": {"plain": {"clips": "models/golem.glb", "idle": "Idle", "walk": "WalkF", "run": "Run"}}})
        states = next(s for s in B.steps_from_blocks(self.mod, self.mod / "assets") if s.get("name") == "plain")["states"]
        self.assertEqual(states["Dead"], {"engine": "Dead", "clip": "golem.Idle", "loop": False}, "with no death clip it stands as in Idle and holds")
        self.assertEqual(states["Stagger"]["clip"], "golem.Idle")

    def test_cook(self):
        self.write({"format": 1, "creatures": {"golem": {"body": BODY, "animation": ANIM}}})
        doc = R.load(self.mod / "assets" / "cook.json")
        self.assertIn("../blocks/golem.json", doc["blocksSources"], "a change in the blocks means a new cook")
        rep = R.Run(self.mod / "assets" / "cook.json", TYPES).run(update_manifest=True)
        self.assertTrue(rep["ok"], rep)
        side = json.loads((self.mod / "assets" / "cooked" / "golem.animation.golem.body.graph.json").read_text())
        slam = side["states"]["slam"]
        self.assertEqual(slam["guid"], G.derive("golem-mod", "state", "slam"), "a state's id is what C# derives for new AnimState(\"slam\")")
        self.assertEqual(side["states"]["Idle"]["id"], "0x1490bed7")
        self.assertEqual(side["skeleton"], G.derive("golem-mod", "skeleton", "golem"))
        self.assertIn("HandR", side["slots"])
        self.assertFalse(R.needs_cook(self.mod / "assets" / "cook.json"))
        self.write({"format": 1, "creatures": {"golem": {"body": BODY, "animation": dict(ANIM, run="Slam")}}})
        self.assertTrue(R.needs_cook(self.mod / "assets" / "cook.json"), "editing a block asks for a cook")

    def test_mistakes(self):
        self.write({"format": 1, "animations": {"a": dict(ANIM, idle=None)}})
        with self.assertRaises(R.RecipeError) as e:
            R.load(self.mod / "assets" / "cook.json")
        self.assertIn("idle", str(e.exception))
        self.write({"format": 1, "animations": {"a": dict(ANIM, states={"Idle": "Idle"})}})
        with self.assertRaises(R.RecipeError) as e:
            R.load(self.mod / "assets" / "cook.json")
        self.assertIn("states.Idle", str(e.exception))

    def test_no_blocks(self):
        doc = R.load(self.mod / "assets" / "cook.json")
        self.assertEqual(doc["steps"], [], "a mod without blocks keeps its recipe as it is")


if __name__ == "__main__":
    unittest.main()
