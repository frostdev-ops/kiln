# SPDX-License-Identifier: MIT
"""Which game-tree nodes of mods get the game's native slots (native/services/skills/tree.cpp, compiled here for Linux and run on
the client's own SkillTreeResource). A pure function of the definitions, because client and server must agree without talking, and as
stable as that can be, because a node that moves costs its players a migration (docs/kiln/services/skills.md "Order and the caps").

    python3 -m unittest discover -s tools/kfc/tests -p test_skilltree_slots.py

Needs the installed client (skips without it) and a C++20 compiler.
"""
import json
import os
import random
import re
import subprocess
import sys
import unittest
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(HERE.parents[0]))
from kfc.archive import Kfc, ResourceKey  # noqa: E402
from kfc.cli import DEFAULT_GAME  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

GAME = Path(os.environ.get("KILN_GAME", DEFAULT_GAME))
OUT = Path(os.environ.get("KILN_OUT", ROOT / ".out/models")) / "skilltree"
TREE = "108ac23f-a77d-4f18-bfac-acc397edaaf0"


def compile_cli():
    OUT.mkdir(parents=True, exist_ok=True)
    exe = OUT / "skilltree_cli"
    src = [HERE / "skilltree_cli.cpp", ROOT / "native/services/skills/tree.cpp"]
    if not exe.exists() or any(s.stat().st_mtime > exe.stat().st_mtime for s in src + [ROOT / "native/services/skills/tree.h"]):
        subprocess.run(["c++", "-std=c++20", "-O1", f"-I{ROOT}/native/third_party", f"-I{ROOT}/native/services/skills", f"-I{ROOT}/native/core/abi",
                        *map(str, src), "-o", str(exe)], check=True)
    return exe


def nodes_of(path, mod):
    text = re.sub(r"^\s*//.*$", "", Path(path).read_text(), flags=re.M)
    return [{"mod": mod, "node": n} for n in json.loads(text)["gameTree"]["nodes"]]


def bulk(mod, n, start=0, parent="0x8efcc472"):
    """n nodes of a mod, each linked from a root."""
    return [{"mod": mod, "node": {"guid": str(uuid.uuid5(uuid.NAMESPACE_DNS, f"{mod}.{i}")), "name": f"{mod}_{i}", "like": "0xcfa5985b", "from": [parent], "cost": 1, "maxLevel": 0}}
            for i in range(start, start + n)]


@unittest.skipUnless(list(GAME.glob("*.kfc")), "no client installed")
class Slots(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.exe = compile_cli()
        exe = GAME / "enshrouded.exe"
        types = TypeDb.from_exe(exe)
        kfc = Kfc(GAME)
        data = kfc.read(ResourceKey(TREE, types.get("keen::SkillTreeResource")["hash"], 0))
        cls.res = OUT / "tree.bin"
        cls.res.write_bytes(data)
        cls.sample = nodes_of(ROOT / "mods/samples/SkillTreeSample/skills/sample.json", "skilltreesample")
        cls.test = nodes_of(ROOT / "mods/tests/skills-test/skills/test.json", "skills-test")

    def build(self, defs, *flags):
        f = OUT / "defs.json"
        f.write_text(json.dumps(defs))
        r = subprocess.run([str(self.exe), str(self.res), str(f), *flags], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        return json.loads(r.stdout)

    @staticmethod
    def native(r):
        return {n["name"] for n in r["nodes"] if n["state"] == 1 and not n["shadow"]}

    def test_the_retest_case_adding_a_big_mod_moves_nothing_of_a_small_one(self):
        alone = self.build(self.sample)
        self.assertEqual(self.native(alone), {"skilltreesample_ember_ward", "skilltreesample_emberheart"})
        both = self.build(self.sample + self.test)
        self.assertLessEqual(self.native(alone), self.native(both), "the sample's nodes stay native when skills-test comes along")
        self.assertEqual(both["shadowNodes"], 7)

    def test_the_client_check_a_small_mod_moves_one_node_of_skills_test(self):
        # skilltest_bulk_7 is the node that crosses the line, both ways
        slot = nodes_of(ROOT / "mods/tests/skills-slot-test/skills/slot.json", "skills-slot-test")
        without = self.native(self.build(self.sample + self.test))
        with_it = self.native(self.build(self.sample + self.test + slot))
        self.assertIn("skilltest_bulk_7", without)
        self.assertEqual(without - with_it, {"skilltest_bulk_7"})
        self.assertEqual(with_it - without, {"skillsslot_one"})

    def test_every_slot_is_used(self):
        r = self.build(self.sample + self.test)
        self.assertEqual(len(self.native(r)) + r["vanilla"], 256, "no slot stays free while nodes wait for one")

    def test_order_of_the_definitions_does_not_matter(self):
        defs = self.sample + self.test + bulk("third", 4)
        first = self.build(defs)
        key = lambda r: sorted((n["id"], n["index"], n["levelIndex"], n["shadow"], n["state"]) for n in r["nodes"])
        for seed in range(5):
            shuffled = defs[:]
            random.Random(seed).shuffle(shuffled)
            self.assertEqual(key(self.build(shuffled)), key(first), "client and server may load mods in any order")

    def test_a_chain_stays_whole_from_its_root(self):
        # the game's walkers follow native lists only: a native Kiln node hangs on a game node or a native node, whatever the ids say
        defs = self.sample + self.test + bulk("third", 4)
        r = self.build(defs)
        native = self.native(r)
        by_name = {d["node"]["name"]: d["node"] for d in defs}
        for name in native:
            parents = by_name[name].get("from", [])
            self.assertTrue(any(p.startswith("0x") or p in native for p in parents), f"{name} hangs on {parents}")
        self.assertIn("skilltreesample_emberheart", native, "its parent's mod keeps the chain together")

    def test_adding_a_mod_only_displaces_bigger_mods(self):
        small = bulk("aaa", 3)
        big = bulk("bigmod", 12)
        before = self.native(self.build(small + big))
        self.assertEqual(len(before), 13)
        after = self.native(self.build(small + big + bulk("newcomer", 5)))
        self.assertLessEqual({n["node"]["name"] for n in small}, after, "the smaller mod keeps its slots")
        self.assertLessEqual({n["node"]["name"] for n in bulk("newcomer", 5)}, after)
        self.assertEqual(len(before - after), 5, "displaced: exactly what came in, from the bigger mod")

    def test_a_mod_keeps_its_slots_through_a_mod_that_is_bigger(self):
        small = bulk("zzz-small", 4)
        before = self.native(self.build(small))
        after = self.native(self.build(small + bulk("aaa-big", 30)))
        self.assertLessEqual(before, after, "its id sorts last, but it is the smaller mod")

    def test_levels_have_their_own_cap(self):
        leveled = [{"mod": "lv", "node": {**n["node"], "maxLevel": 2}} for n in bulk("lv", 12)]
        r = self.build(leveled)
        self.assertEqual(len(self.native(r)), 9, "9 level slots")
        self.assertEqual(r["shadowNodes"], 3)
        indices = sorted(n["levelIndex"] for n in r["nodes"] if not n["shadow"])
        self.assertEqual(indices, list(range(91, 100)), "no gaps")

    def test_a_node_on_no_game_node_stays_native_when_nothing_is_short(self):
        island = [{"mod": "iso", "node": {"guid": str(uuid.uuid5(uuid.NAMESPACE_DNS, f"iso{i}")), "name": f"iso_{i}", "like": "0xcfa5985b", "cost": 1, "maxLevel": 0,
                                          "from": [f"iso_{1 - i}"]}} for i in (0, 1)]
        r = self.build(island + bulk("other", 2))
        self.assertEqual(r["shadowNodes"], 0)
        self.assertEqual(len(self.native(r)), 4)
        # short of slots, it is what gives way
        r = self.build(island + bulk("other", 13))
        self.assertEqual({n["name"] for n in r["nodes"] if n["shadow"]}, {"iso_0", "iso_1"})

    def test_without_the_hooks_past_the_caps_is_refused(self):
        r = self.build(bulk("big", 20), "--no-shadow")
        self.assertEqual(len(self.native(r)), 13)
        self.assertEqual(sum(1 for n in r["nodes"] if n["state"] == 2), 7)
        self.assertEqual(r["shadowNodes"], 0)


if __name__ == "__main__":
    unittest.main()
