#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests of registrygen's analyzer hint table (managed/Kiln.Analyzers/src/Kiln.Analyzers/RegistryHints.g.cs): the old data
paths each registry field covers, made from the schemas and regmap/common alone, so a registry a later lane adds (here a
fixture "creatures" over templates and a hooks registry "limits") is in the table with no other change.

    python3 -I tools/registrygen/tests/test_registry_hints.py        (exit 0 = every test passed)
"""
import json
import re
import shutil
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
sys.path.insert(0, str(HERE.parent))

import registrygen as rg  # noqa: E402

CREATURES = {
    "registrySchema": 1, "registry": "creatures", "typeName": "Creature", "version": "0.1.0", "stability": "experimental",
    "experimentalId": "KILNX031", "doc": "Fixture creatures.",
    "ids": {"game": "game:<stable name>", "mod": "<mod id>:<name>", "rule": "Lower-case template names."},
    "fields": {
        "health": {"type": "struct", "applies": "now", "fields": {
            "perPlayer": {"type": "float", "min": 0, "max": 100, "ops": "number"}}},
        "faction": {"type": "enum", "values": ["vukah", "fell"], "applies": "now", "ops": "set"}},
    "entry": {"add": False, "remove": False}, "raw": "refused",
}
CREATURES_MAP = {
    "regmap": 1, "registry": "creatures",
    "entries": {"kind": "resource", "resourceType": "keen::ecs::TemplateResource", "name": "name", "unnamed": "template_%.8s"},
    "rows": {
        "health.perPlayer": {"paths": ["EnemyScaling.playerCountScaling.healthScaling"], "conv": "float", "read": "now", "backend": "live"},
        "faction": {"paths": ["Faction.faction"], "conv": "enum", "values": {"vukah": "Vukah", "fell": "Fell"}, "read": "now", "backend": "live"}}}
LIMITS = {
    "registrySchema": 1, "registry": "limits", "typeName": "Limits", "version": "0.1.0", "stability": "experimental",
    "experimentalId": "KILNX031", "doc": "Fixture limits.",
    "ids": {"game": "game:limits", "mod": "<mod id>:<name>", "rule": "One entry."},
    "fields": {"maxAltars": {"type": "int", "min": 1, "max": 255, "applies": "now", "ops": "number"}},
    "entry": {"add": False, "remove": False}, "raw": "refused"}
LIMITS_MAP = {
    "regmap": 1, "registry": "limits", "entries": {"kind": "hooks", "unnamed": "limits"},
    "rows": {"maxAltars": {"paths": ["altarCap"], "conv": "int", "read": "now", "backend": "hook",
                           "evidence": "sigdb limits/AltarLimit (server 0x93dea0): min(altarsPerFlameLevel[level], 16)"}}}


def tree_with(*extra):
    """A copy of the repository's schemas and regmap/common, plus fixture registries."""
    import tempfile
    root = Path(tempfile.mkdtemp(prefix="registry-hints-"))
    shutil.copytree(REPO / "schema" / "registry", root / "schema" / "registry")
    shutil.copytree(REPO / "regmap" / "common", root / "regmap" / "common")
    (root / "native" / "services" / "content").mkdir(parents=True)
    shutil.copy(REPO / "native" / "services" / "content" / "gamedata_res.cpp", root / "native" / "services" / "content")
    for schema, mapping in extra:
        (root / "schema" / "registry" / f"{schema['registry']}.json").write_text(json.dumps(schema))
        (root / "regmap" / "common" / f"{mapping['registry']}.json").write_text(json.dumps(mapping))
    rg.update_lock(root)
    return root


def rows(text, table):
    """The tuples of one generated table: ("a", "b", true) per line."""
    body = text.split(f" {table} =", 1)[1].split("};", 1)[0]
    out = []
    for m in re.finditer(r"\(([^()]*)\)", body):
        out.append(tuple(json.loads(x.strip()) for x in m.group(1).split(", ")))
    return out


class HintsTest(unittest.TestCase):
    def test_the_shipped_registries_cover_their_old_paths(self):
        text = rg.render_hints(rg.load(REPO), REPO)
        paths = rows(text, "Paths")
        self.assertIn(("keen::ItemInfo", "maxstacksize", "items.maxStack", True), paths)
        # a formula's inputs are covered, not one to one
        self.assertTrue(any(r[0] == "keen::ItemInfo" and r[2] == "items.power" and not r[3] for r in paths))
        # array entries: their element's paths below the array
        self.assertTrue(any(r[2] == "recipes.duration" and r[1].endswith("[].craftingduration") for r in paths), paths[:5])
        sections = dict(rows(text, "Sections"))
        self.assertEqual("keen::ItemInfo", sections["items"])
        self.assertEqual("keen::ecs::TemplateResource", sections["templates"])
        self.assertTrue(sections["recipes"].endswith("[]"))
        self.assertIn("balance", [r[0] for r in rows(text, "Registries")])
        self.assertNotIn("keen::", "".join(r[2] for r in paths))  # the Kiln side names Kiln fields only

    def test_a_member_is_the_field_whose_own_member_it_is_never_a_formula_that_only_reads_it(self):
        # itemLevelRange.minLevel is items.levels.min (and only an input of the armor and power formulas); damageSetup.speedMod
        # is items.speed (and an input of power): KILN124 names the field a mod edits by changing the member
        paths = rows(rg.render_hints(rg.load(REPO), REPO), "Paths")

        def fields(root, path):
            return sorted((r[2], r[3]) for r in paths if r[0] == root and r[1] == path)
        self.assertEqual([("items.levels.min", True)], fields("keen::ItemInfo", "itemlevelrange.minlevel"))
        self.assertEqual([("items.speed", True)], fields("keen::ItemInfo", "damagesetup.speedmod"))
        self.assertEqual([("items.power", False)], fields("keen::ItemInfo", "damagesetup.dmgmod"))
        self.assertEqual([("items.armor.blunt", False)], fields("keen::ItemInfo", "armorsetup.distribution.blunt"))
        # two fields whose own member it is (two configurations of one list): both stay
        self.assertEqual([("items.manaCost", False), ("items.staminaCost", False)], fields("keen::ItemInfo", "impactvalues"))

    def test_a_configuration_is_named_by_its_config(self):
        # a data pack names one configuration of a list by its name ("impactValues.ManaCost": 30, SpellTweaks): that is the
        # field whose row names that config, not every field of the list
        paths = rows(rg.render_hints(rg.load(REPO), REPO), "Paths")
        self.assertIn(("keen::ItemInfo", "impactvalues.manacost", "items.manaCost", False), paths)
        self.assertIn(("keen::ItemInfo", "impactvalues.staminacost", "items.staminaCost", False), paths)

    def test_a_later_registry_is_picked_up_from_its_schema_and_regmap(self):
        root = tree_with((CREATURES, CREATURES_MAP), (LIMITS, LIMITS_MAP))
        try:
            model = rg.load(root)
            self.assertEqual([], model.errors)
            text = rg.render_hints(model, root)
            paths = rows(text, "Paths")
            self.assertIn(("keen::ecs::TemplateResource", "enemyscaling.playercountscaling.healthscaling", "creatures.health.perPlayer", True), paths)
            # an enum is covered, but its values have other names: not one to one for a fix
            self.assertIn(("keen::ecs::TemplateResource", "faction.faction", "creatures.faction", False), paths)
            self.assertIn(("altarlimit", "limits.maxAltars"), rows(text, "Hooks"))
            self.assertIn("creatures", [r[0] for r in rows(text, "Registries")])
            # and the generated file is one of registrygen's outputs: --check fails while it is stale
            outputs = rg.render(model, rg.read_lock(root))
            self.assertIn(rg.HINTS, outputs)
        finally:
            shutil.rmtree(root)

    def test_gamedata_sections_follow_the_native_table(self):
        # the section -> root type table must name what gamedata_res.cpp builds each section from
        src = (REPO / "native" / "services" / "content" / "gamedata_res.cpp").read_text(encoding="utf-8")
        for section, root in rg.GAMEDATA_SECTIONS.items():
            if section == "itemdata":
                continue
            self.assertRegex(src, r'"%s",\s*"%s"' % (re.escape(section), re.escape(root)))
        self.assertEqual([], rg.section_drift(REPO))

    def test_the_committed_table_is_current(self):
        model = rg.load(REPO)
        want = rg.render_hints(model, REPO)
        have = (REPO / rg.HINTS).read_text(encoding="utf-8")
        self.assertEqual(want, have, "run tools/registrygen/registrygen.py")


if __name__ == "__main__":
    unittest.main()
