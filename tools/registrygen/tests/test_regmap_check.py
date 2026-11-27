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

    def test_a_position_needs_a_signed_64_bit_member(self):
        # dev.32 (world.noBuildZones): a world position's axis is a signed 64-bit 32.32 fixed-point number of metres
        types = copy.deepcopy(TYPES)
        thing = next(t for t in types["types"] if t["name"] == "keen::Thing")
        thing["fields"].append({"name": "x", "type": "keen::sint64", "offset": 56})
        s = copy.deepcopy(SCHEMA)
        s["fields"]["px"] = {"type": "float", "unit": "m", "min": -100000, "max": 100000, "applies": "now", "ops": "number"}
        rows = copy.deepcopy(ROWS)
        rows["px"] = {"paths": ["x"], "conv": "position", "read": "now", "backend": "live"}
        self.assertEqual(self.run_check(rows, s, types).errors, [])
        rows["px"]["paths"] = ["size"]
        errs = self.run_check(rows, s, types).errors
        self.assertTrue(any("things.px" in e and "signed 64-bit" in e for e in errs), errs)

    def test_resize_is_for_a_list_row_of_the_boot_back_end(self):
        # dev.32 review: the native loader (regmap.cpp load_common) refuses "resize" anywhere but on a list row of the boot
        # back-end, and not as true/false; tools/check.sh must fail the same rows before a build turns them off
        rows = copy.deepcopy(ROWS)
        rows["three"]["resize"] = True
        self.assertEqual(self.run_check(rows).errors, [])
        for field, value in (("size", True), ("rows", True), ("three", "yes")):
            bad = copy.deepcopy(ROWS)
            bad[field]["resize"] = value
            errs = self.run_check(bad).errors
            self.assertTrue(any(f"things.{field}" in e and "resize" in e for e in errs), (field, value, errs))

    def test_a_hooks_registry_names_kilns_hook_values(self):
        # dev.32 (limits): no resource; each row names one of Kiln's hook values; restart fields through the hook back-end
        s = copy.deepcopy(SCHEMA)
        s["registry"] = "caps"
        s["fields"] = {"most": {"type": "int", "min": 1, "max": 255, "applies": "restart", "ops": "number"},
                       "reach": {"type": "float", "min": 0, "max": 1000, "applies": "restart", "ops": "number"}}
        t = Path(tempfile.mkdtemp(prefix="regmap-hooks-"))
        try:
            (t / "schema" / "registry").mkdir(parents=True)
            (t / "schema" / "registry" / "caps.json").write_text(json.dumps(s))
            import registrygen
            registrygen.update_lock(t)
            (t / "regmap" / "common").mkdir(parents=True)

            def check(rows, entries=None):
                (t / "regmap" / "common" / "caps.json").write_text(json.dumps({
                    "regmap": 1, "registry": "caps", "entries": entries or {"kind": "hooks", "unnamed": "caps"}, "rows": rows}))
                return rc.check(t, rc.TypeDb.from_refl(TYPES))
            good = {"most": {"paths": ["altarCap"], "conv": "int", "read": "now", "backend": "hook"},
                    "reach": {"paths": ["giftRange"], "conv": "float", "read": "now", "backend": "hook"}}
            r = check(good)
            self.assertEqual(r.errors, [])
            self.assertEqual(r.absent, [])  # no root type to look for
            self.assertEqual(r.resolved, 2)
            bad = copy.deepcopy(good)
            bad["most"]["paths"] = ["altarLimit"]
            self.assertTrue(any("caps.most" in e and "hook values" in e for e in check(bad).errors))
            bad = copy.deepcopy(good)
            bad["reach"]["conv"] = "int"
            self.assertTrue(any("caps.reach" in e and "float" in e for e in check(bad).errors))
            bad = copy.deepcopy(good)
            bad["most"]["backend"] = "boot"
            self.assertTrue(any("caps.most" in e and "hook back-end" in e for e in check(bad).errors))
            r = check(good, {"kind": "hooks", "unnamed": "caps", "resourceType": "keen::Thing"})
            self.assertTrue(any("no resourceType" in e for e in r.errors), r.errors)
        finally:
            shutil.rmtree(t, ignore_errors=True)

    @unittest.skipUnless(Path("/mnt/Dongus/kiln-wave/sfi/out/binaries/server-26430253/enshrouded_server.exe").is_file(), "no pinned server exe")
    def test_hooks_rows_resolve_in_a_scratch_root(self):
        # kilnctl and the lab check a scratch root with only regmap/ and schema/: the hooks rows' sigdb names still resolve
        exe = "/mnt/Dongus/kiln-wave/sfi/out/binaries/server-26430253/enshrouded_server.exe"
        t = Path(tempfile.mkdtemp(prefix="regmap-scratch-"))
        try:
            shutil.copytree(REPO / "schema", t / "schema")
            (t / "regmap").mkdir()
            shutil.copytree(REPO / "regmap" / "common", t / "regmap" / "common")
            db = rc.TypeDb.from_refl({"types": [], "exe_sha256": "x"})
            db.exe_path, db.side = exe, "server"
            names = db.hook_names(t)
            self.assertTrue({"limits/AltarLimit", "limits/GiftRange", "limits/altars.cap", "limits/altars.table"} <= names, names)
        finally:
            shutil.rmtree(t, ignore_errors=True)

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
