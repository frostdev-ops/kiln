#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests of tools/registrygen/regmap_ids.py: the Kiln ids of game entries (the same cases the native assign_ids is tested
on, native/services/content/tests/registry_ids_cases.json) and the alias deltas a per-build record carries from one game
build to the next. Made-up entries, no game data.

    python3 -I tools/registrygen/tests/test_regmap_ids.py
"""
import json
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
sys.path.insert(0, str(HERE.parent))

import regmap_ids as ri  # noqa: E402

CASES = REPO / "native" / "services" / "content" / "tests" / "registry_ids_cases.json"
ITEMS = {"unnamed": "item_%08x"}
DROPS = {"nameFrom": "keen::ecs::DefaultInventory.defaultInventory", "unnamed": "drops_%.8s"}


def E(guid, name="", id=0, by=None):
    return {"guid": guid, "name": name, "id": id, "namedBy": by or []}


G = ["%08x-0000-0000-0000-%012x" % (0x10000000 * (i + 1), i + 1) for i in range(8)]


class SameAsNative(unittest.TestCase):
    def test_cases(self):
        doc = json.loads(CASES.read_text())
        for c in doc["cases"]:
            by = {int(k): v for k, v in c.get("byItemId", {}).items()} if "byItemId" in c else None
            by_guid = c.get("byKeyGuid")
            ids, notes = ri.derive(c["registry"], c["src"], c["aliases"], c["entries"], by, by_guid)
            self.assertEqual(ids, c["expect"], c["name"])
            self.assertEqual(len(notes), c.get("notes", 0), (c["name"], notes))
            for i in ids:
                self.assertTrue(ri.KILN_ID.match(i), i)


class AcrossBuilds(unittest.TestCase):
    """Build 1, then build 2 of a made-up game: what the record of build 2 must hold so that no id moves."""

    def record(self, registry, src, entries, previous=None, table=None, build="2"):
        return ri.record_aliases(registry, src, previous or {}, table or {}, entries, build)

    def test_loot_entries_keyed_by_item_id_are_kept_across_builds(self):
        # loot entries have no GUID: kiln.content's id table keys them "#<32-bit id>" (the kept table of the previous build
        # holds those rows). The next build has the same entries: none of them is removed, and no id is retired
        # (the first real update rehearsal, 26404334 -> 26430253, retired all 4,213 and blocked the proof).
        loot = {"key": "items", "unnamed": "item_%08x"}
        items = {1: "game:sword", 2: "game:axe"}
        entries = [E("", "", 1), E("", "", 2)]
        prev = {"#1": "game:sword", "#2": "game:axe"}
        al, ids, table = ri.record_aliases("loot", loot, {}, prev, entries, "2", items)
        self.assertEqual(ids, ["game:sword", "game:axe"])
        self.assertEqual(al["removed"], {})
        self.assertEqual(table, prev)
        # an entry the new build no longer has is removed, as for any registry
        al, ids, table = ri.record_aliases("loot", loot, {}, prev, entries[:1], "2", items)
        self.assertEqual(al["removed"], {"game:axe": "2"})

    def test_first_build(self):
        al, ids, table = self.record("items", ITEMS, [E(G[0], "Sword", 1), E(G[1], "Sword", 2), E(G[2], "Axe", 3)], build="1")
        self.assertEqual(ids, ["game:sword", f"game:sword~{G[1][:8]}", "game:axe"])
        # the holder of a clashed name and the suffixed one are pinned at once: an order change cannot move them
        self.assertEqual(al["pinned"], {G[0]: "game:sword", G[1]: f"game:sword~{G[1][:8]}"})
        self.assertEqual(al["removed"], {})
        self.assertEqual(table, {G[0]: "game:sword", G[1]: f"game:sword~{G[1][:8]}", G[2]: "game:axe"})

    def test_order_change_keeps_ids(self):
        al1, ids1, t1 = self.record("items", ITEMS, [E(G[0], "Sword", 1), E(G[1], "Sword", 2)], build="1")
        al2, ids2, t2 = self.record("items", ITEMS, [E(G[1], "Sword", 2), E(G[0], "Sword", 1)], al1, t1)
        self.assertEqual(ids2, [f"game:sword~{G[1][:8]}", "game:sword"])
        self.assertEqual(t2, t1)

    def test_new_entry_with_a_taken_name(self):
        al1, _, t1 = self.record("items", ITEMS, [E(G[0], "Sword", 1)], build="1")
        self.assertEqual(al1["pinned"], {})                 # no clash, nothing to pin
        al2, ids2, t2 = self.record("items", ITEMS, [E(G[3], "Sword", 4), E(G[0], "Sword", 1)], al1, t1)
        self.assertEqual(ids2, [f"game:sword~{G[3][:8]}", "game:sword"])   # the first holder keeps it for good
        self.assertEqual(al2["pinned"], {G[0]: "game:sword", G[3]: f"game:sword~{G[3][:8]}"})

    def test_rename_keeps_the_old_id(self):
        al1, _, t1 = self.record("items", ITEMS, [E(G[0], "Sword", 1)], build="1")
        al2, ids2, _ = self.record("items", ITEMS, [E(G[0], "Blade", 1)], al1, t1)
        self.assertEqual(ids2, ["game:sword"])
        self.assertEqual(al2["pinned"], {G[0]: "game:sword"})

    def test_removal_retires_the_id(self):
        al1, _, t1 = self.record("items", ITEMS, [E(G[0], "Sword", 1), E(G[1], "Axe", 2)], build="1")
        al2, ids2, t2 = self.record("items", ITEMS, [E(G[1], "Axe", 2)], al1, t1, build="2")
        self.assertEqual(al2["removed"], {"game:sword": "2"})
        self.assertNotIn(G[0], t2)
        # build 3: a new entry with the removed entry's name never gets its id
        al3, ids3, _ = self.record("items", ITEMS, [E(G[1], "Axe", 2), E(G[4], "Sword", 5)], al2, t2, build="3")
        self.assertEqual(ids3, ["game:axe", f"game:sword~{G[4][:8]}"])
        self.assertEqual(al3["removed"], {"game:sword": "2"})

    def test_pins_carry_forward(self):
        al1, _, t1 = self.record("items", ITEMS, [E(G[0], "Sword", 1), E(G[1], "Sword", 2)], build="1")
        al2, _, t2 = self.record("items", ITEMS, [E(G[0], "Sword", 1), E(G[1], "Sword", 2)], al1, t1)
        self.assertEqual(al2["pinned"], al1["pinned"])
        # without the previous id table, the record's pins alone still hold every id
        al3, ids3, _ = self.record("items", ITEMS, [E(G[1], "Sword", 2), E(G[0], "Sword", 1)], al2, {})
        self.assertEqual(ids3, [f"game:sword~{G[1][:8]}", "game:sword"])

    def test_drop_table_naming_change(self):
        al1, ids1, t1 = self.record("drops", DROPS, [E(G[0], by=["Chest_B", "Chest_C"])], build="1")
        self.assertEqual(ids1, ["game:chest_b"])
        # build 2: a template that sorts first now uses the table too; the table keeps its id
        al2, ids2, _ = self.record("drops", DROPS, [E(G[0], by=["Chest_A", "Chest_B", "Chest_C"])], al1, t1)
        self.assertEqual(ids2, ["game:chest_b"])
        self.assertEqual(al2["pinned"], {G[0]: "game:chest_b"})

    def test_sanitized_names_are_pinned(self):
        al, ids, _ = self.record("items", ITEMS, [E(G[0], "Bob's Sword", 1)], build="1")
        self.assertEqual(ids, ["game:bob_s_sword"])
        self.assertEqual(al["pinned"], {G[0]: "game:bob_s_sword"})

    def test_stations_keep_their_ids_across_builds(self):
        # dev.32: a station takes the id of the item that builds it (keyMember) and keeps it by its GUID. An update that gives a
        # station another item, none, or one where it had none pins the id it had; a new station built by the old item gets a
        # suffix, never that id; the record's pins alone (no previous table) hold every id.
        st = {"key": "items", "keyMember": "item", "unnamed": "station_%08x"}

        def S(guid, station_id, item):
            return dict(E(guid, "", station_id), keyId=item)

        items1 = {5: "game:workbench", 6: "game:forge"}
        b1 = [S(G[0], 0xA1, 5), S(G[1], 0xA2, 6), S(G[2], 0xA3, 0)]
        al1, ids1, t1 = ri.record_aliases("stations", st, {}, {}, b1, "1", items1)
        self.assertEqual(ids1, ["game:workbench", "game:forge", "game:station_000000a3"])
        self.assertEqual(al1, {"pinned": {}, "removed": {}})
        items2 = {5: "game:workbench", 6: "game:forge", 7: "game:workbench_v2", 8: "game:altar"}
        b2 = [S(G[3], 0xA4, 5), S(G[0], 0xA1, 7), S(G[1], 0xA2, 0), S(G[2], 0xA3, 8)]
        al2, ids2, t2 = ri.record_aliases("stations", st, al1, t1, b2, "2", items2)
        self.assertEqual(ids2, [f"game:workbench~{G[3][:8]}", "game:workbench", "game:forge", "game:station_000000a3"])
        self.assertEqual(al2["removed"], {})
        self.assertEqual({G[0], G[1], G[2]} - set(al2["pinned"]), set())
        al3, ids3, _ = ri.record_aliases("stations", st, al2, {}, b2, "3", items2)
        self.assertEqual(ids3, ids2)

    def test_skill_nodes_without_a_guid_keep_their_ids_across_builds(self):
        # dev.32: a skill node has no GUID; its id is its 32-bit node id and its key in the id table "#<id>": the same node keeps
        # its id, a node the update no longer has is removed, never given again
        sk = {"unnamed": "skill_%08x"}
        n1 = [E("", "", 0x4475E8DD), E("", "", 0x8EFCC472)]
        al1, ids1, t1 = ri.record_aliases("skills", sk, {}, {}, n1, "1")
        self.assertEqual(ids1, ["game:skill_4475e8dd", "game:skill_8efcc472"])
        self.assertEqual(t1, {f"#{0x4475E8DD}": "game:skill_4475e8dd", f"#{0x8EFCC472}": "game:skill_8efcc472"})
        al2, ids2, t2 = ri.record_aliases("skills", sk, al1, t1, [E("", "", 0x8EFCC472)], "2")
        self.assertEqual(ids2, ["game:skill_8efcc472"])
        self.assertEqual(al2["removed"], {"game:skill_4475e8dd": "2"})

    def test_record_shape(self):
        al, _, _ = self.record("items", ITEMS, [E(G[0], "Sword", 1), E(G[1], "Sword", 2)], build="1")
        rec = ri.aliases_json({"items": al})
        self.assertEqual(rec["suffixes"], [{"registry": "items", "guid": G[1], "id": f"game:sword~{G[1][:8]}"}])
        self.assertEqual(rec["renames"], [{"registry": "items", "guid": G[0], "id": "game:sword"}])
        back = ri.aliases_from_json(rec)
        self.assertEqual(back["items"]["pinned"], al["pinned"])



class Entries(unittest.TestCase):
    """regmap_entries.py (the offline entries regmap_check --record and kilnctl regproof read) takes what makes a template a
    creature from the mapping it proves (regmap/common/creatures.json entries.withComponent), never a list of its own."""

    def test_creature_components_come_from_the_mapping(self):
        import importlib.util, tempfile
        spec = importlib.util.spec_from_file_location("regmap_entries", HERE.parent / "regmap_entries.py")
        re_ = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(re_)
        want = json.loads((REPO / "regmap" / "common" / "creatures.json").read_text(encoding="utf-8"))["entries"]["withComponent"]
        self.assertEqual(list(re_.creature_components()), want)
        with tempfile.TemporaryDirectory() as d:
            common = Path(d) / "regmap" / "common"
            common.mkdir(parents=True)
            (common / "creatures.json").write_text(json.dumps({"registry": "creatures", "entries": {"withComponent": ["Pet", "keen::Mount"]}}))
            self.assertEqual(re_.creature_components(Path(d)), ("Pet", "Mount"))  # a qualified type matches by its last part


if __name__ == "__main__":
    unittest.main(verbosity=1)
