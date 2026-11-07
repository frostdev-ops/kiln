# SPDX-License-Identifier: MIT
"""Models of placed things in blocks/*.json -> model steps (cook/placed.py). Offline, no game files.

    python3 -m unittest discover -s tools/modelcook/tests -p 'test_placed.py' -v
"""
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "kfc"))
from cook import placed as PL  # noqa: E402
from cook import recipe as R  # noqa: E402


class Placed(unittest.TestCase):
    def test_steps(self):
        with tempfile.TemporaryDirectory() as t:
            d = Path(t)
            (d / "blocks").mkdir()
            (d / "models").mkdir()
            for f in ("lamp.glb", "bench.gltf"):
                (d / "models" / f).write_bytes(b"glTF")
            (d / "blocks" / "a.json").write_text(json.dumps({
                "looks": {"bench-look": {"model": "models/bench.gltf"}},
                "props": {"lamp": {"name": "Lamp", "visual": {"model": "models/lamp.glb"}}, "lamp2": {"name": "Lamp", "visual": {"model": "models/lamp.glb"}},
                          "vase": {"name": "Vase", "visual": {"like": "Prop_Decoration_T2_Vase_Clay_01"}}, "gone": {"name": "x", "visual": {"model": "models/none.glb"}}},
                "seats": {"bench": {"name": "Bench", "visual": "bench-look"}},
            }))
            steps = PL.steps_from_blocks(d, d / "assets")
            self.assertEqual([s["placed"] for s in steps], ["models/lamp.glb", "models/bench.gltf"], "each file once; a missing one is the block loader's to name")
            self.assertEqual(steps[0]["gltf"], "../models/lamp.glb")
            self.assertEqual((steps[0]["op"], steps[0]["mode"], steps[0]["materials"], steps[0]["name"], steps[0]["stem"]), ("model", "new", "gltf", "models/lamp", "models_lamp"))
            self.assertEqual(steps[0]["tool"], "modelcook", "mixed recipes must dispatch these steps to modelcook")

    def test_malformed_sections_do_not_break_model_discovery(self):
        with tempfile.TemporaryDirectory() as t:
            d = Path(t)
            (d / "blocks").mkdir()
            for i, value in enumerate([[], 42, {"looks": []}, {"props": ["bad"]}]):
                (d / "blocks" / f"{i}.json").write_text(json.dumps(value))
            self.assertEqual(PL.files_from_blocks(d), [])

    def test_placed_models_are_cooked_even_when_the_recipe_defaults_to_soundcook(self):
        with tempfile.TemporaryDirectory() as t:
            d = Path(t)
            for folder in ("blocks", "models", "assets"):
                (d / folder).mkdir()
            (d / "models/lamp.glb").write_bytes(b"glTF")
            (d / "blocks/p.json").write_text(json.dumps({"props": {"lamp": {"visual": {"model": "models/lamp.glb"}}}}))
            p = d / "assets/cook.json"
            p.write_text(json.dumps({"format": 1, "tool": "soundcook", "steps": []}))
            self.assertEqual([st["placed"] for _, st in R.own_steps(R.load(p))], ["models/lamp.glb"])


if __name__ == "__main__":
    unittest.main()
