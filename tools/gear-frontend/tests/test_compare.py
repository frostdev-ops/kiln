#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests of tools/gear-frontend/compare.py on made-up dumps (no game data).

    python3 -I tools/gear-frontend/tests/test_compare.py
"""
import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import compare  # noqa: E402

BAL = "82706b40-61b1-4b8f-8b23-dcec6971bda1"
FRONT = [{"mod": "gear-frontend-test", "key": "gearRules@gear.json", "state": "registry", "fields": 9}]


def dump(items=None, bal="b1", fronts=None, targets=None):
    return {"items": items or {"g1": "a1", "g2": "a2"}, "balance": {BAL: {"sha256": bal}},
            "targets": targets or {"Sword": {"damageSetup.dmgMod": "1.5"}}, "registry": {"frontEnds": fronts} if fronts else {}}


class Compare(unittest.TestCase):
    def test_three_equal_boots_pass(self):
        problems, facts = compare.compare(dump(), dump(fronts=FRONT), dump())
        self.assertEqual(problems, [])
        self.assertTrue(any("-> registry (9 fields)" in f for f in facts))

    def test_an_item_that_differs_fails_and_names_the_target_values(self):
        reg = dump(items={"g1": "a1", "g2": "zz"}, targets={"Sword": {"damageSetup.dmgMod": "1.6"}})
        problems, _ = compare.compare(dump(), dump(fronts=FRONT), reg)
        self.assertTrue(any(p.startswith("item g2: patch a2 != registry zz") for p in problems))
        self.assertTrue(any("Sword.damageSetup.dmgMod: patch 1.5 / registry 1.6" in p for p in problems))

    def test_a_balancing_table_that_differs_fails(self):
        problems, _ = compare.compare(dump(), dump(bal="b2", fronts=FRONT), dump())
        self.assertTrue(any(p.startswith("balance " + BAL) for p in problems))

    def test_the_front_boot_must_have_the_registry_write_its_gear_rules(self):
        kept = copy.deepcopy(FRONT)
        kept[0]["state"] = "resourcePatch"
        self.assertTrue(compare.compare(dump(), dump(fronts=kept), dump())[0])
        self.assertTrue(compare.compare(dump(), dump(), dump())[0])

    def test_the_patch_and_registry_boots_must_have_no_front_ends(self):
        self.assertTrue(compare.compare(dump(fronts=FRONT), dump(fronts=FRONT), dump())[0])
        self.assertTrue(compare.compare(dump(), dump(fronts=FRONT), dump(fronts=FRONT))[0])

    def test_an_empty_dump_fails(self):
        self.assertTrue(compare.compare({"items": {}, "balance": {}}, dump(fronts=FRONT), dump())[0])


if __name__ == "__main__":
    unittest.main()
