#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests of tools/registrygen/regmap_check.py on made-up types (no game data) and on the repository's regmap/.

    python3 -I tools/registrygen/tests/test_regmap_check.py
"""
import copy
import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
sys.path.insert(0, str(HERE.parent))

import regmap_check as rc  # noqa: E402

# Made-up reflection records in tools/kfc refl.extract's shape (kinds as refl: 2 u8, 4 u16, 6 u32, 10 f32, 12 enum,
# 13 bitmask8, 17 typedef, 18 struct, 19 static array, 24 blob array, 28 object reference, 29 guid).
def T(name, kind, size, inner=None, fields=None, values=None, layout=0x1234, count=0):
    t = {"name": name, "kind": kind, "size": size, "inner": inner, "layout": layout, "count": count}
    if fields is not None:
        t["fields"] = [{"name": n, "type": ty, "offset": o} for n, ty, o in fields]
    if values is not None:
        t["values"] = [{"name": n, "value": v} for n, v in values]
    return t

TYPES = {"types": [
    T("keen::uint16", 4, 2), T("keen::uint32", 6, 4), T("keen::uint8", 2, 1), T("keen::float32", 10, 4), T("keen::bool", 1, 1),
    T("keen::sint64", 9, 8), T("keen::Guid", 29, 16),
    T("keen::Time", 18, 8, fields=[("value", "keen::sint64", 0)]),
    T("keen::Rarity", 12, 1, inner="keen::uint8", values=[("Common", 0), ("Rare", 1)]),
    T("keen::Bitmask8<keen::Rarity>", 13, 1, inner="keen::Rarity"),
    T("keen::RarityMask", 17, 1, inner="keen::Bitmask8<keen::Rarity>"),
    T("keen::ThingId", 17, 4, inner="keen::uint32"),
    T("keen::ObjectReference<keen::Thing>", 28, 16),
    T("keen::StaticArray<keen::uint32,3>", 19, 12, inner="keen::uint32", count=3),
    T("keen::Row", 18, 8, fields=[("n", "keen::uint32", 0), ("x", "keen::float32", 4)]),
    T("keen::BlobArray<keen::Row>", 24, 8, inner="keen::Row"),
    T("keen::Thing", 18, 64, layout=0xabcdef01, fields=[
        ("size", "keen::uint16", 0), ("color", "keen::Rarity", 2), ("mask", "keen::RarityMask", 3), ("on", "keen::bool", 4),
        ("time", "keen::Time", 8), ("id", "keen::ThingId", 16), ("ref", "keen::ObjectReference<keen::Thing>", 20),
        ("three", "keen::StaticArray<keen::uint32,3>", 36), ("rows", "keen::BlobArray<keen::Row>", 48), ("debugName", "keen::Guid", 0)]),
]}

SCHEMA = {
    "registrySchema": 1, "registry": "things", "version": "1.0.0", "stability": "experimental", "experimentalId": "KILNX031",
    "doc": "Test.", "ids": {"game": "game:<stable name>", "mod": "<mod id>:<name>", "rule": "Lower case."}, "traits": {},
    "fields": {
        "size": {"type": "int", "min": 1, "max": 65535, "applies": "worldLoad", "ops": "number"},
        "color": {"type": "enum", "values": ["common", "rare"], "applies": "now", "ops": "set"},
        "mask": {"type": "flags", "values": ["common", "rare"], "applies": "now", "ops": "set"},
        "on": {"type": "bool", "applies": "now", "ops": "set"},
        "time": {"type": "time", "min": 0, "max": 100, "applies": "now", "ops": "number"},
        "other": {"type": "ref", "registry": "things", "applies": "now", "ops": "set"},
        "three": {"type": "list", "of": {"type": "int", "min": 0, "max": 9}, "maxItems": 3, "applies": "restart", "ops": "list"},
        "rows": {"type": "list", "applies": "now", "ops": "list", "of": {"type": "struct", "fields": {
            "n": {"type": "int", "min": 0, "max": 9, "ops": "number"}, "x": {"type": "float", "min": 0, "max": 1, "ops": "number"}}}},
    },
    "entry": {"add": True, "remove": "hidden"}, "raw": "allowed",
}

ROWS = {
    "size": {"paths": ["size"], "conv": "int", "read": "worldLoad", "backend": "live"},
    "color": {"paths": ["color"], "conv": "enum", "values": {"common": "Common", "rare": "Rare"}, "read": "now", "backend": "live"},
    "mask": {"paths": ["mask"], "conv": "flags", "values": {"common": "Common", "rare": "Rare"}, "read": "now", "backend": "live"},
    "on": {"paths": ["on"], "conv": "bool", "read": "now", "backend": "live"},
    "time": {"paths": ["time"], "conv": "time", "read": "now", "backend": "live"},
    "other": {"paths": ["id", "ref"], "conv": "ref", "read": "now", "backend": "live"},
    "three": {"paths": ["three"], "conv": "list", "read": "restart", "backend": "boot"},
    "rows": {"paths": ["rows"], "conv": "list", "read": "now", "backend": "live"},
    "rows[].n": {"paths": ["n"], "conv": "int", "read": "now", "backend": "live"},
    "rows[].x": {"paths": ["x"], "conv": "float", "read": "now", "backend": "live"},
}


class Tree:
    def __init__(self, rows=None, schema=None):
        self.root = Path(tempfile.mkdtemp(prefix="regmap-check-"))
        (self.root / "schema" / "registry").mkdir(parents=True)
        (self.root / "schema" / "registry" / "things.json").write_text(json.dumps(schema or SCHEMA))
        sys.path.insert(0, str(HERE.parent))
        import registrygen
        registrygen.update_lock(self.root)
        (self.root / "regmap" / "common").mkdir(parents=True)
        (self.root / "regmap" / "common" / "things.json").write_text(json.dumps({
            "regmap": 1, "registry": "things", "entries": {"kind": "resource", "resourceType": "keen::Thing", "name": "debugName"},
            "rows": ROWS if rows is None else rows}))

    def close(self):
        shutil.rmtree(self.root, ignore_errors=True)


class Checks(unittest.TestCase):
    def run_check(self, rows=None, schema=None, types=TYPES):
        t = Tree(rows, schema)
        try:
            return rc.check(t.root, rc.TypeDb.from_refl(types))
        finally:
            t.close()

    def test_good_rows_pass(self):
        r = self.run_check()
        self.assertEqual(r.errors, [])
        self.assertEqual(r.resolved, len(ROWS))

    def test_missing_member(self):
        rows = copy.deepcopy(ROWS)
        rows["size"]["paths"] = ["sizes"]
        errs = self.run_check(rows).errors
        self.assertTrue(any("things.size" in e and "sizes" in e for e in errs), errs)

    def test_kind_mismatch(self):
        rows = copy.deepcopy(ROWS)
        rows["on"]["paths"] = ["size"]
        self.assertTrue(any("things.on" in e and "bool" in e for e in self.run_check(rows).errors))
        rows = copy.deepcopy(ROWS)
        rows["time"]["paths"] = ["size"]
        self.assertTrue(any("things.time" in e for e in self.run_check(rows).errors))

    def test_range_must_fit_the_storage(self):
        s = copy.deepcopy(SCHEMA)
        s["fields"]["size"]["max"] = 70000
        errs = self.run_check(schema=s).errors
        self.assertTrue(any("things.size" in e and "65535" in e for e in errs), errs)

    def test_enum_names_must_exist(self):
        rows = copy.deepcopy(ROWS)
        rows["color"]["values"]["rare"] = "Mythic"
        self.assertTrue(any("things.color" in e and "Mythic" in e for e in self.run_check(rows).errors))
        rows = copy.deepcopy(ROWS)
        del rows["mask"]["values"]["rare"]
        self.assertTrue(any("things.mask" in e and "rare" in e for e in self.run_check(rows).errors))

    def test_static_array_holds_max_items(self):
        s = copy.deepcopy(SCHEMA)
        s["fields"]["three"]["maxItems"] = 4
        self.assertTrue(any("things.three" in e and "3" in e for e in self.run_check(schema=s).errors))

    def test_list_elements_fit_their_storage(self):
        s = copy.deepcopy(SCHEMA)
        s["fields"]["three"]["of"]["max"] = 5000000000
        self.assertTrue(any("things.three" in e and "4294967295" in e for e in self.run_check(schema=s).errors))

    def test_routing(self):
        rows = copy.deepcopy(ROWS)
        rows["size"]["read"] = "restart"
        self.assertTrue(any("things.size" in e and "restart" in e for e in self.run_check(rows).errors))
        rows = copy.deepcopy(ROWS)
        rows["three"]["backend"] = "live"
        self.assertTrue(any("things.three" in e and "boot" in e for e in self.run_check(rows).errors))

    def test_untraced_read_time_ships_the_cautious_applies(self):
        rows = copy.deepcopy(ROWS)
        rows["color"].update({"read": "unknown", "state": "unproven", "reason": "when the game reads the color is not traced"})
        self.assertTrue(any("things.color" in e and "cautious" in e for e in self.run_check(rows).errors))

    def test_reasons_in_kiln_words(self):
        rows = copy.deepcopy(ROWS)
        rows["on"].update({"read": "now", "state": "unproven", "reason": "the reader of mask is not traced"})
        self.assertEqual(self.run_check(rows).errors, [])
        schema = copy.deepcopy(SCHEMA)
        rows["three"]["paths"] = ["threeValues"]
        rows["on"]["reason"] = "the reader of threeValues is not traced"
        TYPES2 = copy.deepcopy(TYPES)
        TYPES2["types"][-1]["fields"].append({"name": "threeValues", "type": "keen::StaticArray<keen::uint32,3>", "offset": 36})
        errs = self.run_check(rows, schema, TYPES2).errors
        self.assertTrue(any("things.on" in e and "threeValues" in e for e in errs), errs)

    def test_record_carries_and_computes_aliases(self):
        t = Tree()
        try:
            db = rc.TypeDb.from_refl(dict(TYPES, exe_sha256="ab" * 32))
            G1, G2 = "10000000-0000-0000-0000-000000000001", "20000000-0000-0000-0000-000000000002"
            maps = t.root / "regmap" / "common"
            d = json.loads((maps / "things.json").read_text())
            d["entries"]["unnamed"] = "thing_%08x"
            (maps / "things.json").write_text(json.dumps(d))
            # the tool takes its registries in load order; a test registry rides as "items"
            (maps / "things.json").rename(maps / "items.json")
            d["registry"] = "items"
            (maps / "items.json").write_text(json.dumps(d))
            e1 = {"registries": {"items": [{"guid": G1, "name": "Sword", "id": 1}, {"guid": G2, "name": "Sword", "id": 2}]}}
            rec1, table1 = rc.record(t.root, db, "server", "1", "", e1, None, None)
            self.assertEqual(rec1["aliases"]["renames"], [{"registry": "items", "guid": G1, "id": "game:sword"}])
            self.assertEqual(rec1["aliases"]["suffixes"], [{"registry": "items", "guid": G2, "id": "game:sword~20000000"}])
            e2 = {"registries": {"items": [{"guid": G2, "name": "Sword", "id": 2}]}}
            rec2, table2 = rc.record(t.root, db, "server", "2", "", e2, rec1, table1)
            self.assertEqual(rec2["aliases"]["removals"], [{"registry": "items", "id": "game:sword", "removedIn": "2"}])
            self.assertEqual(table2, {"items": {G2: "game:sword~20000000"}})
            rec3, _ = rc.record(t.root, db, "server", "3", "", None, rec2, None)      # no entries: carried as they are
            self.assertEqual(rec3["aliases"], rec2["aliases"])
        finally:
            t.close()

    def test_record_pins_template_names_outside_the_grammar(self):
        # templates have no regmap file of their own (the model's builtin source names them); their pins ride in the record
        t = Tree()
        try:
            db = rc.TypeDb.from_refl(dict(TYPES, exe_sha256="ab" * 32))
            G = "9a0ade54-4473-4eca-9fd4-e2b01051f2e0"
            e = {"registries": {"templates": [{"guid": G, "name": "VFX_Ball flower_deflected", "id": 0},
                                              {"guid": "11111111-0000-0000-0000-000000000001", "name": "Plain", "id": 0}]}}
            rec, table = rc.record(t.root, db, "server", "1", "", e, None, None)
            self.assertIn({"registry": "templates", "guid": G, "id": "game:vfx_ball_flower_deflected"}, rec["aliases"]["renames"])
            self.assertEqual(len(rec["aliases"]["renames"]), 1)  # only the name outside the grammar: no full name list
            self.assertEqual(table["templates"][G], "game:vfx_ball_flower_deflected")
        finally:
            t.close()

    def test_the_run_time_id_table_is_read_as_the_previous_table(self):
        # review finding 4: kiln.content's table ({"format", ..., "registries": {...}}) feeds --ids-in as the tool's own does
        G = "30000000-0000-0000-0000-000000000003"
        for wrap in (lambda tb: tb, lambda tb: {"format": 1, "side": "server", "exeSha256": "ab" * 32, "decoder": "x", "registries": tb}):
            t = Tree()
            try:
                db = rc.TypeDb.from_refl(dict(TYPES, exe_sha256="ab" * 32))
                maps = t.root / "regmap" / "common"
                d = json.loads((maps / "things.json").read_text())
                d["registry"] = "items"
                (maps / "things.json").rename(maps / "items.json")
                (maps / "items.json").write_text(json.dumps(d))
                rec1, table1 = rc.record(t.root, db, "server", "1", "", {"registries": {"items": [{"guid": G, "name": "Axe", "id": 3}]}}, None, None)
                # build 2: the game renamed the entry's debug name; the previous build's table keeps its Kiln id
                _, table2 = rc.record(t.root, db, "server", "2", "", {"registries": {"items": [{"guid": G, "name": "Axe_Renamed", "id": 3}]}}, rec1, wrap(table1))
                self.assertEqual(table2["items"][G], "game:axe")
            finally:
                t.close()

    def test_missing_rows_are_unmapped_not_errors(self):
        rows = copy.deepcopy(ROWS)
        del rows["on"]
        r = self.run_check(rows)
        self.assertEqual(r.errors, [])
        self.assertEqual(r.unmapped, ["things.on"])

    def test_types_not_in_the_dump_are_counted(self):
        r = self.run_check(types={"types": [t for t in TYPES["types"] if t["name"] != "keen::Thing"]})
        self.assertEqual(r.errors, [])
        self.assertEqual(r.resolved, 0)
        self.assertEqual(r.absent, ["keen::Thing"])

    def test_record_from_the_proof(self):
        t = Tree()
        try:
            db = rc.TypeDb.from_refl(dict(TYPES, exe_sha256="ab" * 32))
            rec, _ = rc.record(t.root, db, "server", "1", "2")
            self.assertTrue(rec["verified"])
            self.assertEqual(rec["layouts"], {"keen::Thing": "0xabcdef01"})
            self.assertEqual(rec["rows"]["things.size"], "ok")
            self.assertEqual(rec["aliases"], {"renames": [], "removals": [], "suffixes": []})
            rows = copy.deepcopy(ROWS)
            rows["size"]["paths"] = ["gone"]
            (t.root / "regmap" / "common" / "things.json").write_text(json.dumps({
                "regmap": 1, "registry": "things", "entries": {"kind": "resource", "resourceType": "keen::Thing"}, "rows": rows}))
            rec, _ = rc.record(t.root, db, "server", "1", "2")
            self.assertEqual(rec["rows"]["things.size"], "unmapped")
        finally:
            t.close()

    def test_sigdb_dump_shape(self):
        dump = {"schema": 2, "side": "server", "exe_sha256": "00" * 32, "components": [], "events": [], "types": [
            {"name": "Row", "qualifiedName": "keen::Row", "hash": "0x0", "kind": 18, "size": 8, "align": 4,
             "fields": [{"name": "n", "offset": 0, "type": "keen::uint32"}]},
            {"name": "uint32", "qualifiedName": "keen::uint32", "hash": "0x0", "kind": 6, "size": 4, "align": 4}]}
        db = rc.TypeDb.from_dump(dump)
        self.assertEqual(db.member_type("keen::Row", "n")["name"], "keen::uint32")


# dev.32 creatures: a template's paths start at a component by its type name, and an attribute's slot is an index
TEMPLATE_TYPES = {"types": TYPES["types"] + [
    T("keen::StaticArray<keen::uint32,2>", 19, 8, inner="keen::uint32", count=2),
    T("keen::ecs::Component", 18, 0, fields=[]),
    T("keen::ecs::Level", 18, 16, inner="keen::ecs::Component", fields=[("dataStorage", "keen::StaticArray<keen::uint32,2>", 8)]),
    T("keen::ecs::Side", 18, 1, inner="keen::ecs::Component", fields=[("side", "keen::Rarity", 0)]),
    T("keen::ecs::Prowl", 18, 4, inner="keen::ecs::Component", fields=[("range", "keen::float32", 0)]),
    T("keen::ecs::Herd", 18, 16, inner="keen::ecs::Component", fields=[("home", "keen::ObjectReference<keen::Thing>", 0)]),
    T("keen::ecs::TemplateResource", 18, 24, fields=[("name", "keen::Guid", 0), ("components", "keen::BlobArray<keen::Row>", 16)]),
]}
BEASTS = {
    "registrySchema": 1, "registry": "beasts", "version": "0.1.0", "stability": "experimental", "experimentalId": "KILNX031",
    "doc": "Test.", "ids": {"game": "game:<stable name>", "mod": "<mod id>:<name>", "rule": "Lower case."}, "traits": {},
    "fields": {
        "kind": {"type": "enum", "values": ["prowler", "herder"], "readOnly": True, "applies": "now"},
        "level": {"type": "int", "min": 1, "max": 100, "applies": "now", "ops": "number"},
        "side": {"type": "enum", "values": ["common", "rare"], "applies": "now", "ops": "set"},
        "home": {"type": "ref", "registry": "beasts", "readOnly": True, "applies": "now"},
    },
    "entry": {"add": False, "remove": False}, "raw": "allowed",
}
BEAST_ROWS = {
    "kind": {"paths": ["Prowl", "Herd"], "conv": "component", "values": {"prowler": "Prowl", "herder": "Herd"}, "read": "now", "backend": "none"},
    "level": {"paths": ["Level.dataStorage[0]"], "conv": "int", "read": "now", "backend": "live"},
    "side": {"paths": ["Side.side"], "conv": "enum", "values": {"common": "Common", "rare": "Rare"}, "read": "now", "backend": "live"},
    "home": {"paths": ["Herd.home"], "conv": "ref", "read": "now", "backend": "none"},
}


class TemplateRoots(unittest.TestCase):
    def run_beasts(self, rows=None, entries=None):
        root = Path(tempfile.mkdtemp(prefix="regmap-check-"))
        try:
            (root / "schema" / "registry").mkdir(parents=True)
            (root / "schema" / "registry" / "beasts.json").write_text(json.dumps(BEASTS))
            import registrygen
            registrygen.update_lock(root)
            (root / "regmap" / "common").mkdir(parents=True)
            e = entries or {"kind": "resource", "resourceType": "keen::ecs::TemplateResource", "name": "name", "key": "templates",
                            "withComponent": ["Prowl", "Herd"]}
            (root / "regmap" / "common" / "beasts.json").write_text(json.dumps({"regmap": 1, "registry": "beasts", "entries": e,
                                                                                 "rows": BEAST_ROWS if rows is None else rows}))
            return rc.check(root, rc.TypeDb.from_refl(TEMPLATE_TYPES))
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_component_paths_indexes_and_kind_rows_pass(self):
        r = self.run_beasts()
        self.assertEqual(r.errors, [])
        self.assertEqual(r.resolved, len(BEAST_ROWS))

    def test_an_index_past_a_static_array_fails(self):
        rows = copy.deepcopy(BEAST_ROWS)
        rows["level"]["paths"] = ["Level.dataStorage[2]"]
        errs = self.run_beasts(rows).errors
        self.assertTrue(any("beasts.level" in e and "2 elements" in e for e in errs), errs)

    def test_a_first_part_that_is_no_component_fails(self):
        rows = copy.deepcopy(BEAST_ROWS)
        rows["side"]["paths"] = ["Sides.side"]
        errs = self.run_beasts(rows).errors
        self.assertTrue(any("beasts.side" in e and "not a component type" in e for e in errs), errs)

    def test_a_component_row_maps_every_path_and_schema_value(self):
        rows = copy.deepcopy(BEAST_ROWS)
        rows["kind"]["values"] = {"prowler": "Prowl"}
        errs = self.run_beasts(rows).errors
        self.assertTrue(any("beasts.kind" in e and "one Kiln name to each of its paths" in e for e in errs), errs)
        self.assertTrue(any("beasts.kind" in e and "herder" in e for e in errs), errs)

    def test_a_one_path_ref_is_the_reference_alone(self):
        rows = copy.deepcopy(BEAST_ROWS)
        rows["home"]["paths"] = ["Level.dataStorage[0]"]
        errs = self.run_beasts(rows).errors
        self.assertTrue(any("beasts.home" in e and "the reference" in e for e in errs), errs)

    def test_with_component_names_component_types_of_templates(self):
        errs = self.run_beasts(entries={"kind": "resource", "resourceType": "keen::ecs::TemplateResource", "name": "name",
                                        "withComponent": ["Prowl", "Nope"]}).errors
        self.assertTrue(any("withComponent Nope" in e for e in errs), errs)
        errs = self.run_beasts(entries={"kind": "resource", "resourceType": "keen::Thing", "name": "debugName", "withComponent": ["Prowl"]}).errors
        self.assertTrue(any("withComponent is a list of component types of template resources" in e for e in errs), errs)
# A sequence-like resource for dev.32's abilities: a blob array of sub-sequences whose events are variants of a base event
# (paths with an index and a [Type#k] selector, the eventTime conversion).
SEQ_TYPES = {"types": TYPES["types"] + [
    T("keen::sint64", 9, 8),
    T("keen::Event", 18, 16, fields=[("time", "keen::Time", 0), ("duration", "keen::Time", 8)]),
    T("keen::BaseSpawn", 18, 24, inner="keen::Event", fields=[("count", "keen::uint8", 16)]),
    T("keen::act::Spawn", 18, 32, inner="keen::BaseSpawn", fields=[("radius", "keen::float32", 24)]),
    T("keen::ds::Spawn", 18, 32, fields=[("time", "keen::Time", 0)]),            # same last name, another base
    T("keen::act::Pay", 18, 24, inner="keen::Event"),
    T("keen::BlobVariant<keen::Event>", 27, 12, inner="keen::Event"),
    T("keen::BlobArray<keen::BlobVariant<keen::Event>>", 24, 8, inner="keen::BlobVariant<keen::Event>"),
    T("keen::Events", 17, 8, inner="keen::BlobArray<keen::BlobVariant<keen::Event>>"),
    T("keen::BlobString", 25, 8),
    T("keen::Sub", 18, 24, fields=[("events", "keen::Events", 0), ("length", "keen::Time", 8), ("name", "keen::BlobString", 16)]),
    T("keen::BlobArray<keen::Sub>", 24, 8, inner="keen::Sub"),
    T("keen::Seq", 18, 24, fields=[("id", "keen::Guid", 0), ("subs", "keen::BlobArray<keen::Sub>", 16)]),
]}

SEQ_SCHEMA = {
    "registrySchema": 1, "registry": "things", "version": "1.0.0", "stability": "experimental", "experimentalId": "KILNX031",
    "doc": "Test.", "ids": {"game": "game:<stable name>", "mod": "(none)", "rule": "Lower case."}, "traits": {},
    "fields": {"release": {"type": "time", "min": 0, "max": 600, "applies": "now", "ops": "number"},
               "length": {"type": "time", "min": 0, "max": 600, "applies": "now", "ops": "number"}},
    "entry": {"add": False, "remove": False}, "raw": "refused",
}
SEQ_ROWS = {"release": {"paths": ["subs[0].events[keen::act::Spawn#0].time"], "conv": "eventTime", "read": "now", "backend": "live"},
            "length": {"paths": ["subs[0].length"], "conv": "time", "read": "now", "backend": "live"}}


class Selectors(unittest.TestCase):
    def check(self, rows=None, name="subs[0].name"):
        t = Tree(rows if rows is not None else SEQ_ROWS, SEQ_SCHEMA)
        try:
            common = t.root / "regmap" / "common" / "things.json"
            d = json.loads(common.read_text())
            d["entries"] = {"kind": "resource", "resourceType": "keen::Seq", "name": name}
            common.write_text(json.dumps(d))
            return rc.check(t.root, rc.TypeDb.from_refl(SEQ_TYPES))
        finally:
            t.close()

    def test_an_index_and_a_variant_selector_resolve(self):
        r = self.check()
        self.assertEqual(r.errors, [])
        self.assertEqual(r.resolved, 2)
        db = rc.TypeDb.from_refl(SEQ_TYPES)
        t, err = db.path("keen::Seq", "subs[0].events[keen::act::Spawn#3].radius")
        self.assertIsNone(err)
        self.assertEqual(t["name"], "keen::float32")
        t, err = db.path("keen::Seq", "subs[0].events[Spawn#0].count")  # a last name: the one variant of the base
        self.assertIsNone(err, err)
        self.assertEqual(t["name"], "keen::uint8")

    def test_a_selector_names_a_variant_of_the_lists_base(self):
        db = rc.TypeDb.from_refl(SEQ_TYPES)
        for path, why in (("subs[0].events[keen::ds::Spawn#0].time", "keen::Event"),     # another base
                          ("subs[0].events[keen::act::Nope#0].time", "keen::act::Nope"),
                          ("subs[0].length[0]", "not a list"),                           # an index on a scalar
                          ("subs[0].events[keen::act::Spawn#x].time", "#x"),
                          ("subs[x].length", "[x]"),
                          ("subs[0].events[0].time", "variant")):                         # a plain index into variants
            t, err = db.path("keen::Seq", path)
            self.assertIsNotNone(err, path)
            self.assertIn(why, err, path)

    def test_entry_names_resolve_through_an_index(self):
        self.assertEqual(self.check(name="subs[0].name").errors, [])
        errs = self.check(name="subs[0].label").errors
        self.assertTrue(any("entries.name" in e and "label" in e for e in errs), errs)

    def test_a_rows_variant_selector_names_its_type_qualified(self):
        # review of dev.32: kilnctl dumps only the variant types a path names qualified and kiln.content's eventTime binder finds
        # the event by its qualified type, so a last name would pass here and fail every lab proof and every edit
        rows = copy.deepcopy(SEQ_ROWS)
        rows["release"]["paths"] = ["subs[0].events[Spawn#0].time"]
        errs = self.check(rows).errors
        self.assertTrue(any("things.release" in e and "qualified" in e for e in errs), errs)
        self.assertEqual(self.check().errors, [])

    def test_event_time_names_the_first_event_of_a_kind(self):
        rows = copy.deepcopy(SEQ_ROWS)
        rows["release"]["paths"] = ["subs[0].events[keen::act::Spawn#1].time"]
        errs = self.check(rows).errors
        self.assertTrue(any("things.release" in e and "#0" in e for e in errs), errs)
        rows["release"]["paths"] = ["subs[0].length"]
        errs = self.check(rows).errors
        self.assertTrue(any("things.release" in e and "#0" in e for e in errs), errs)
        rows["release"]["paths"] = ["subs[0].events[keen::act::Spawn#0].count"]
        errs = self.check(rows).errors
        self.assertTrue(any("things.release" in e and "keen::Time" in e for e in errs), errs)


class Records(unittest.TestCase):
    """review3 S3: a committed verified record is bound to the tree's regmap/common (regmapDigest), so an edit of
    regmap/common that was not re-proven fails the check (kiln.content would enable no row of that build)."""

    def tree_with_record(self, **record):
        t = Tree()
        self.addCleanup(t.close)
        (t.root / "regmap" / "server").mkdir()
        rec = {"regmap": 1, "side": "server", "exeSha256": "ab" * 32, "verified": True, "rows": {}, "provenBy": "lab"}
        rec.update(record)
        (t.root / "regmap" / "server" / f"{'ab' * 32}.json").write_text(json.dumps(rec))
        return t

    def test_digest_is_kilnctls(self):
        t = Tree()
        self.addCleanup(t.close)
        import hashlib
        body = (t.root / "regmap" / "common" / "things.json").read_bytes()
        want = hashlib.sha256(f"things.json\n{hashlib.sha256(body).hexdigest()}\n".encode()).hexdigest()
        self.assertEqual(rc.common_digest(t.root), want)

    def test_a_record_of_the_trees_common_passes(self):
        t = Tree()
        self.addCleanup(t.close)
        t = self.tree_with_record(regmapDigest=rc.common_digest(t.root))
        self.assertEqual(rc.record_problems(t.root), [])
        self.assertEqual(rc.main(["--root", str(t.root)]), 0)

    def test_an_edit_of_common_that_was_not_re_proven_fails(self):
        t = Tree()
        self.addCleanup(t.close)
        t = self.tree_with_record(regmapDigest=rc.common_digest(t.root))
        f = t.root / "regmap" / "common" / "things.json"
        f.write_text(f.read_text() + "\n")  # even a cosmetic edit: every row of the build would go off
        problems = rc.record_problems(t.root)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("regmapDigest", problems[0])
        self.assertEqual(rc.main(["--root", str(t.root)]), 1)

    def test_a_stray_file_in_common_fails(self):
        t = Tree()
        self.addCleanup(t.close)
        t = self.tree_with_record(regmapDigest=rc.common_digest(t.root))
        shutil.copy(t.root / "regmap" / "common" / "things.json", t.root / "regmap" / "common" / "things - Copy.json")
        self.assertEqual(len(rc.record_problems(t.root)), 1)

    def test_a_verified_record_without_a_digest_fails(self):
        t = self.tree_with_record()
        self.assertEqual(len(rc.record_problems(t.root)), 1)

    def test_an_instance_marked_record_is_not_committed(self):
        # review3 S4: kilnctl regproof --mark's instance copy says "markedLocally"; the tree takes the --out copy
        t = Tree()
        self.addCleanup(t.close)
        t = self.tree_with_record(regmapDigest=rc.common_digest(t.root), markedLocally=True)
        problems = rc.record_problems(t.root)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("markedLocally", problems[0])

    def test_a_verified_server_record_is_lab_proven(self):
        # review3 S5: the gate copy's reflection-only candidate (verified, every row ok) must never be committed as a proof
        t = Tree()
        self.addCleanup(t.close)
        for proven_by in (None, "mirror"):
            t = self.tree_with_record(regmapDigest=rc.common_digest(t.root), provenBy=proven_by)
            problems = rc.record_problems(t.root)
            self.assertEqual(len(problems), 1, problems)
            self.assertIn("provenBy", problems[0])

    def test_an_unverified_record_enables_nothing_and_passes(self):
        t = self.tree_with_record(verified=False, regmapDigest="00" * 32)
        self.assertEqual(rc.record_problems(t.root), [])


class Repository(unittest.TestCase):
    def test_committed_records_are_bound_to_the_trees_common(self):
        self.assertEqual(rc.record_problems(REPO), [])

    def test_repository_against_the_committed_dumps(self):
        for side in ("server", "client"):
            for dump in sorted((REPO / "sigdb" / "types" / side).glob("*.json")):
                d = json.loads(dump.read_text())
                if d.get("schema") != 2:
                    continue
                r = rc.check(REPO, rc.TypeDb.from_dump(d))
                self.assertEqual(r.errors, [], f"{dump.name}: {r.errors}")

    @unittest.skipUnless(os.environ.get("KILN_SERVER_EXE"), "KILN_SERVER_EXE (a copy of the server exe) not set")
    def test_repository_against_the_server_exe(self):
        r = rc.check(REPO, rc.TypeDb.from_exe(os.environ["KILN_SERVER_EXE"]))
        self.assertEqual(r.errors, [])
        self.assertEqual(r.absent, [])


if __name__ == "__main__":
    unittest.main(verbosity=1)
