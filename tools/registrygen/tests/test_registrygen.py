#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests of tools/registrygen/registrygen.py: schema checks, the ids lock, migrations, generated outputs.

    python3 -I tools/registrygen/tests/test_registrygen.py        (exit 0 = every test passed)
"""
import copy
import json
import re
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
sys.path.insert(0, str(HERE.parent))

import registrygen as rg  # noqa: E402

MINI = {
    "registrySchema": 1, "registry": "things", "typeName": "Thing", "version": "0.1.0",
    "stability": "experimental", "experimentalId": "KILNX031", "doc": "Test things.",
    "ids": {"game": "game:<stable name>", "mod": "<mod id>:<name>", "rule": "Lower-case debug names."},
    "traits": {},
    "fields": {
        "size": {"type": "int", "min": 1, "max": 10, "applies": "worldLoad", "ops": "number", "doc": "Size."},
        "color": {"type": "enum", "values": ["red", "blue"], "applies": "now", "ops": "set"},
        "parts": {"type": "struct", "applies": "restart", "fields": {
            "a": {"type": "float", "min": 0, "max": 1, "ops": "number"}, "b": {"type": "bool", "ops": "set", "applies": "now"}}},
        "tags": {"type": "list", "of": {"type": "ref", "registry": "things"}, "maxItems": 3, "applies": "now", "ops": "list"},
        "rows": {"type": "list", "applies": "now", "ops": "list", "of": {"type": "struct", "typeName": "ThingRow", "fields": {
            "n": {"type": "int", "min": 0, "max": 9, "ops": "number"}, "inner": {"type": "struct", "fields": {"x": {"type": "float", "min": 0, "max": 1, "ops": "number"}}}}}},
        "fixed": {"type": "int", "min": 0, "max": 5, "readOnly": True, "applies": "restart"},
    },
    "entry": {"add": True, "remove": "hidden"},
    "raw": "allowed",
}


def migration(tree, frm, to, ids=None, fields=None, name="things"):
    mig = tree.root / "schema" / "registry" / "migrations"
    mig.mkdir(exist_ok=True)
    (mig / f"{name}-{frm}-{to}.json").write_text(json.dumps({"registry": name, "from": frm, "to": to, "ids": ids or {}, "fields": fields or {}}))


class Tree:
    """A made-up repository root with schema/registry/things.json."""

    def __init__(self, schema=None, lock=None):
        self.root = Path(tempfile.mkdtemp(prefix="registrygen-test-"))
        (self.root / "schema" / "registry").mkdir(parents=True)
        self.write(schema or MINI)
        if lock is not None:
            (self.root / "schema" / "registry" / "ids.lock.json").write_text(json.dumps(lock))

    def write(self, schema, name="things"):
        (self.root / "schema" / "registry" / f"{name}.json").write_text(json.dumps(schema))

    def lock(self):
        return json.loads((self.root / "schema" / "registry" / "ids.lock.json").read_text())

    def close(self):
        shutil.rmtree(self.root, ignore_errors=True)


class SchemaChecks(unittest.TestCase):
    def errors_of(self, schema):
        t = Tree(schema)
        try:
            return rg.load(t.root).errors
        finally:
            t.close()

    def test_joined_client_marker_is_additive_and_generated(self):
        s = copy.deepcopy(MINI)
        s["fields"]["size"]["joinedClient"] = "worldLoad"
        t = Tree(s)
        try:
            model = rg.load(t.root)
            self.assertEqual(model.errors, [])
            rg.update_lock(t.root)
            doc = rg.render_doc(model, t.lock(), model.registries["things"])
            self.assertIn("Joined client", doc)
            self.assertIn("worldLoad", doc)
            cs = rg.render_cs_catalog(model, t.lock())
            self.assertIn('JoinedClient = "worldLoad"', cs)
        finally:
            t.close()

    def test_invalid_joined_client_marker_is_refused(self):
        s = copy.deepcopy(MINI)
        s["fields"]["size"]["joinedClient"] = "laterMaybe"
        self.assertTrue(any("joinedClient" in e for e in self.errors_of(s)))

    # owner 2026-11-26 ("make it a registry"): a schema may declare how many edits of it one call takes; registrygen carries
    # it into the native descriptor, RegistrySchema.EditsPerCall and the page; without it, 4096
    def test_edits_per_call_is_declared_per_registry_and_generated(self):
        for value in (16384, 4096):
            s = copy.deepcopy(MINI)
            s["editsPerCall"] = value
            t = Tree(s)
            try:
                model = rg.load(t.root)
                self.assertEqual(model.errors, [])
                rg.update_lock(t.root)
                lock = t.lock()
                self.assertIn(f", {value}u}},", rg.render_native(model, lock))
                self.assertIn(f'["things"] = {value},', rg.render_cs_catalog(model, lock))
                self.assertIn(f"Edits per call: {value:,}.", rg.render_doc(model, lock, model.registries["things"]))
            finally:
                t.close()
        t = Tree(MINI)
        try:
            model = rg.load(t.root)
            rg.update_lock(t.root)
            self.assertIn(", 4096u},", rg.render_native(model, t.lock()))
            self.assertIn('["things"] = 4096,', rg.render_cs_catalog(model, t.lock()))
            self.assertIn("Edits per call: 4,096. ", rg.render_doc(model, t.lock(), model.registries["things"]))
        finally:
            t.close()
        for bad in (0, 4095, 16385, 5000.5, "8192", True):
            s = copy.deepcopy(MINI)
            s["editsPerCall"] = bad
            self.assertTrue(any("editsPerCall" in e for e in self.errors_of(s)), bad)

    def test_mini_is_valid(self):
        self.assertEqual(self.errors_of(MINI), [])

    def test_leaf_without_applies_is_refused(self):
        s = copy.deepcopy(MINI)
        del s["fields"]["size"]["applies"]
        errs = self.errors_of(s)
        self.assertTrue(any("things.size" in e and "applies" in e for e in errs), errs)

    def test_struct_leaf_inherits_applies(self):
        m = rg.load(Tree(MINI).root)
        by = {f.path: f for f in m.registries["things"].leaves}
        self.assertEqual(by["parts.a"].applies, "restart")
        self.assertEqual(by["parts.b"].applies, "now")
        self.assertEqual(by["rows[].inner.x"].applies, "now")

    def test_ops_must_fit_the_type(self):
        s = copy.deepcopy(MINI)
        s["fields"]["size"]["ops"] = "list"
        self.assertTrue(any("things.size" in e and "ops" in e for e in self.errors_of(s)))
        s = copy.deepcopy(MINI)
        s["fields"]["color"]["ops"] = "number"
        self.assertTrue(any("things.color" in e for e in self.errors_of(s)))

    def test_read_only_takes_no_ops(self):
        s = copy.deepcopy(MINI)
        s["fields"]["fixed"]["ops"] = "number"
        self.assertTrue(any("things.fixed" in e and "read-only" in e for e in self.errors_of(s)))

    def test_ref_to_an_unknown_registry(self):
        s = copy.deepcopy(MINI)
        s["fields"]["tags"]["of"]["registry"] = "nowhere"
        self.assertTrue(any("nowhere" in e for e in self.errors_of(s)))

    def test_kiln_names_only(self):
        s = copy.deepcopy(MINI)
        s["fields"]["Size"] = s["fields"].pop("size")
        self.assertTrue(any("Size" in e for e in self.errors_of(s)))
        s = copy.deepcopy(MINI)
        s["fields"]["color"]["values"] = ["Red", "blue"]
        self.assertTrue(any("Red" in e for e in self.errors_of(s)))
        s = copy.deepcopy(MINI)
        s["fields"]["size"]["doc"] = "maxStackSize at +0x14"
        self.assertTrue(any("offset" in e for e in self.errors_of(s)))

    def test_every_number_has_a_range(self):
        for path in (("size",), ("parts", "fields", "a"), ("rows", "of", "fields", "n")):
            s = copy.deepcopy(MINI)
            node = s["fields"]
            for p in path[:-1]:
                node = node[p]
            del node[path[-1]]["max"]
            errs = self.errors_of(s)
            self.assertTrue(any("min and max" in e for e in errs), (path, errs))
        s = copy.deepcopy(MINI)
        s["fields"]["tags"] = {"type": "list", "of": {"type": "int", "min": 0}, "applies": "now", "ops": "list"}
        self.assertTrue(any("things.tags[]" in e and "min and max" in e for e in self.errors_of(s)))

    def test_ids_are_lower_case(self):
        s = copy.deepcopy(MINI)
        s["ids"]["game"] = "Game:<stable name>"
        self.assertTrue(any("ids.game" in e and "lower case" in e for e in self.errors_of(s)))

    def test_min_above_max(self):
        s = copy.deepcopy(MINI)
        s["fields"]["size"]["min"] = 20
        self.assertTrue(any("things.size" in e and "min" in e for e in self.errors_of(s)))

    def test_experimental_needs_its_id(self):
        s = copy.deepcopy(MINI)
        del s["experimentalId"]
        self.assertTrue(any("experimentalId" in e for e in self.errors_of(s)))

    def test_list_of_lists_is_refused(self):
        s = copy.deepcopy(MINI)
        s["fields"]["deep"] = {"type": "list", "applies": "now", "ops": "list", "of": {"type": "list", "of": {"type": "int", "min": 0, "max": 1}}}
        self.assertTrue(any("things.deep" in e for e in self.errors_of(s)))

    def test_optional_refs(self):
        s = copy.deepcopy(MINI)
        s["fields"]["other"] = {"type": "ref", "registry": "things", "optional": True, "applies": "now", "ops": "set"}
        self.assertEqual(self.errors_of(s), [])
        s["fields"]["size"]["optional"] = True
        self.assertTrue(any("things.size" in e and "optional" in e for e in self.errors_of(s)))

    def test_fixed_length_lists(self):
        s = copy.deepcopy(MINI)
        s["fields"]["three"] = {"type": "list", "of": {"type": "int", "min": 0, "max": 9}, "fixedLength": 3, "applies": "restart", "ops": "indexed"}
        self.assertEqual(self.errors_of(s), [])
        s["fields"]["three"]["ops"] = "list"
        self.assertTrue(any("things.three" in e and "indexed" in e for e in self.errors_of(s)))
        s["fields"]["three"] = {"type": "list", "of": {"type": "int", "min": 0, "max": 9}, "lengthField": "size", "lengthOffset": 1, "applies": "restart", "ops": "indexed"}
        self.assertTrue(any("things.three" in e and "read-only" in e for e in self.errors_of(s)))   # size is writable
        s["fields"]["three"]["lengthField"] = "fixed"
        self.assertEqual(self.errors_of(s), [])
        s["fields"]["three"]["lengthField"] = "nowhere"
        self.assertTrue(any("nowhere" in e for e in self.errors_of(s)))
        s["fields"]["tags"]["ops"] = "indexed"
        self.assertTrue(any("things.tags" in e for e in self.errors_of(s)))   # indexed needs a fixed length

    def test_duplicate_type_names(self):
        s = copy.deepcopy(MINI)
        s["fields"]["parts"]["typeName"] = "ThingRow"
        self.assertTrue(any("ThingRow" in e and "twice" in e for e in self.errors_of(s)))
        s = copy.deepcopy(MINI)
        s["fields"]["parts"]["typeName"] = "RegistryId"
        self.assertTrue(any("RegistryId" in e for e in self.errors_of(s)))

    def test_game_member_names_are_refused(self):
        t = Tree(MINI)
        try:
            (t.root / "regmap" / "common").mkdir(parents=True)
            (t.root / "regmap" / "common" / "things.json").write_text(json.dumps({"regmap": 1, "registry": "things",
                "entries": {"kind": "resource", "resourceType": "keen::ThingInfo"}, "rows": {"size": {"paths": ["maxStackSize"]}}}))
            for where in ("doc", "rule", "trait", "removeDoc"):
                s = copy.deepcopy(MINI)
                text = "uses maxStackSize"
                if where == "doc":
                    s["fields"]["size"]["doc"] = text
                elif where == "rule":
                    s["ids"]["rule"] = text
                elif where == "trait":
                    s["traits"] = {"big": text}
                else:
                    s["entry"]["removeDoc"] = text
                t.write(s)
                errs = rg.load(t.root).errors
                self.assertTrue(any("maxStackSize" in e for e in errs), (where, errs))
            s = copy.deepcopy(MINI)
            s["ids"]["rule"] = "named after keen::ThingInfo"
            t.write(s)
            self.assertTrue(any("ThingInfo" in e for e in rg.load(t.root).errors))
        finally:
            t.close()

    def test_flattened_paths(self):
        m = rg.load(Tree(MINI).root)
        paths = [f.path for f in m.registries["things"].rows]
        self.assertEqual(paths, ["size", "color", "parts.a", "parts.b", "tags", "rows", "rows[].n", "rows[].inner.x", "fixed"])


class Lock(unittest.TestCase):
    def setUp(self):
        self.t = Tree()

    def tearDown(self):
        self.t.close()

    def test_unlocked_names_fail_the_check(self):
        m = rg.load(self.t.root)
        errs = rg.lock_errors(m, rg.read_lock(self.t.root))
        self.assertTrue(any("things.size" in e and "ids.lock.json" in e for e in errs), errs)

    def test_lock_assigns_ids_in_order(self):
        rg.update_lock(self.t.root)
        lock = self.t.lock()
        reg = lock["registries"]["things"]
        self.assertEqual(reg["id"], 1)
        self.assertEqual(reg["fields"]["size"]["id"], 1)
        self.assertEqual(reg["fields"]["fixed"]["id"], 9)
        self.assertEqual(reg["fields"]["size"]["since"], "0.1.0")
        self.assertEqual(rg.lock_errors(rg.load(self.t.root), rg.read_lock(self.t.root)), [])

    def test_lock_is_stable(self):
        rg.update_lock(self.t.root)
        first = self.t.lock()
        rg.update_lock(self.t.root)
        self.assertEqual(first, self.t.lock())

    def errs(self):
        return rg.lock_errors(rg.load(self.t.root), rg.read_lock(self.t.root))

    def test_removed_field_needs_retire(self):
        rg.update_lock(self.t.root)
        s = copy.deepcopy(MINI)
        del s["fields"]["color"]
        s["version"] = "0.2.0"
        self.t.write(s)
        migration(self.t, "0.1.0", "0.2.0")
        self.assertTrue(any("things.color" in e and "retire" in e for e in self.errs()), self.errs())
        rg.retire(self.t.root, "things.color", "0.2.0")
        rg.update_lock(self.t.root)
        self.assertEqual(self.errs(), [])
        self.assertEqual(self.t.lock()["registries"]["things"]["retired"]["color"]["retiredIn"], "0.2.0")

    def test_retire_needs_a_breaking_step(self):
        rg.update_lock(self.t.root)
        with self.assertRaises(rg.LockError) as e:
            rg.retire(self.t.root, "things.color", "0.1.1")   # in 0.x the minor acts as the major
        self.assertIn("0.2.0", str(e.exception))
        with self.assertRaises(rg.LockError):
            rg.retire(self.t.root, "things.color", "0.1.0")

    def test_retired_name_is_never_reused(self):
        rg.update_lock(self.t.root)
        rg.retire(self.t.root, "things.color", "0.2.0")
        s = copy.deepcopy(MINI)
        s["version"] = "0.2.0"
        self.t.write(s)
        migration(self.t, "0.1.0", "0.2.0")
        self.assertTrue(any("things.color" in e and "retired" in e for e in self.errs()), self.errs())  # color is back
        with self.assertRaises(rg.LockError):
            rg.update_lock(self.t.root)

    def test_new_ids_never_reuse_retired_ones(self):
        rg.update_lock(self.t.root)
        s = copy.deepcopy(MINI)
        del s["fields"]["fixed"]
        s["fields"]["weight"] = {"type": "int", "min": 0, "max": 9, "applies": "now", "ops": "number"}
        s["version"] = "0.2.0"
        self.t.write(s)
        migration(self.t, "0.1.0", "0.2.0")
        rg.retire(self.t.root, "things.fixed", "0.2.0")
        rg.update_lock(self.t.root)
        reg = self.t.lock()["registries"]["things"]
        self.assertEqual(reg["retired"]["fixed"]["id"], 9)
        self.assertEqual(reg["fields"]["weight"]["id"], 10)
        self.assertEqual(reg["fields"]["weight"]["since"], "0.2.0")

    def test_breaking_step_needs_a_migration(self):
        rg.update_lock(self.t.root)
        s = copy.deepcopy(MINI)
        s["version"] = "0.2.0"
        s["fields"]["size"]["max"] = 5          # narrower: breaking
        self.t.write(s)
        self.assertTrue(any("migrations/things-0.1.0-0.2.0.json" in e for e in self.errs()), self.errs())
        with self.assertRaises(rg.LockError) as e:
            rg.update_lock(self.t.root)          # the reviewer's case: lock may not raise the version without it
        self.assertIn("migrations/things-0.1.0-0.2.0.json", str(e.exception))
        migration(self.t, "0.1.0", "0.2.0")
        rg.update_lock(self.t.root)
        self.assertEqual(self.errs(), [])
        lock = self.t.lock()["registries"]["things"]
        self.assertEqual([h["version"] for h in lock["history"]], ["0.1.0", "0.2.0"])
        self.assertEqual(lock["fields"]["size"]["max"], 5)
        # the migration file removed afterwards: --check finds the step without it
        (self.t.root / "schema" / "registry" / "migrations" / "things-0.1.0-0.2.0.json").unlink()
        self.assertTrue(any("migrations/things-0.1.0-0.2.0.json" in e for e in self.errs()), self.errs())

    def test_stable_major(self):
        s = copy.deepcopy(MINI)
        s["version"] = "1.0.0"
        self.t.write(s)
        rg.update_lock(self.t.root)
        s["version"] = "1.1.0"
        s["fields"]["size"]["min"] = 2            # narrower within 1.x
        self.t.write(s)
        self.assertTrue(any("things.size" in e and "2.0.0" in e for e in self.errs()), self.errs())
        s["version"] = "2.0.0"
        self.t.write(s)
        self.assertTrue(any("migrations/things-1.0.0-2.0.0.json" in e for e in self.errs()))
        migration(self.t, "1.0.0", "2.0.0")
        rg.update_lock(self.t.root)
        self.assertEqual(self.errs(), [])

    def test_migration_shape(self):
        rg.update_lock(self.t.root)
        s = copy.deepcopy(MINI)
        s["version"] = "0.2.0"
        self.t.write(s)
        migration(self.t, "0.1.0", "0.2.0", fields={"size": "nowhere"})
        self.assertTrue(any("nowhere" in e for e in self.errs()), self.errs())
        migration(self.t, "0.1.0", "0.2.0", ids={"game:a": "Game:B"})
        self.assertTrue(any("Game:B" in e for e in self.errs()), self.errs())
        (self.t.root / "schema" / "registry" / "migrations" / "things-0.1.0-0.2.0.json").write_text(json.dumps({"ids": []}))
        self.assertTrue(any("things-0.1.0-0.2.0.json" in e for e in self.errs()), self.errs())

    def test_additive_changes_within_a_minor(self):
        rg.update_lock(self.t.root)
        s = copy.deepcopy(MINI)
        s["version"] = "0.1.1"
        s["fields"]["size"]["max"] = 20                     # wider
        s["fields"]["color"]["values"] = ["red", "blue", "green"]   # appended
        s["fields"]["size"]["applies"] = "now"              # earlier
        s["fields"]["weight"] = {"type": "int", "min": 0, "max": 9, "applies": "now", "ops": "number"}  # new
        self.t.write(s)
        rg.update_lock(self.t.root)
        self.assertEqual(self.errs(), [])
        f = self.t.lock()["registries"]["things"]["fields"]
        self.assertEqual(f["size"]["max"], 20)
        self.assertEqual(f["color"]["values"], ["red", "blue", "green"])

    def test_breaking_changes_within_a_minor(self):
        cases = {
            "narrower": lambda s: s["fields"]["size"].__setitem__("max", 5),
            "reordered": lambda s: s["fields"]["color"].__setitem__("values", ["blue", "red"]),
            "value gone": lambda s: s["fields"]["color"].__setitem__("values", ["red"]),
            "kind": lambda s: s["fields"]["size"].__setitem__("type", "float"),
            "ops": lambda s: s["fields"]["size"].__setitem__("ops", "set"),
            "later": lambda s: s["fields"]["color"].__setitem__("applies", "restart"),
            "read-only": lambda s: s["fields"]["parts"]["fields"]["a"].__setitem__("readOnly", True) or s["fields"]["parts"]["fields"]["a"].pop("ops"),
        }
        for name, change in cases.items():
            t = Tree()
            try:
                rg.update_lock(t.root)
                s = copy.deepcopy(MINI)
                s["version"] = "0.1.1"
                change(s)
                t.write(s)
                errs = rg.lock_errors(rg.load(t.root), rg.read_lock(t.root))
                self.assertTrue(any("0.2.0" in e for e in errs), (name, errs))
                with self.assertRaises(rg.LockError, msg=name):
                    rg.update_lock(t.root)
            finally:
                t.close()

    def test_a_change_needs_a_version_bump(self):
        rg.update_lock(self.t.root)
        s = copy.deepcopy(MINI)
        s["fields"]["size"]["max"] = 20
        self.t.write(s)
        self.assertTrue(any("things.size" in e and "version" in e for e in self.errs()), self.errs())

    def test_lock_records_each_fields_contract(self):
        rg.update_lock(self.t.root)
        f = self.t.lock()["registries"]["things"]["fields"]
        self.assertEqual(f["size"], {"id": 1, "since": "0.1.0", "kind": "int", "applies": "worldLoad", "ops": "number", "readOnly": False, "min": 1, "max": 10})
        self.assertEqual(f["color"]["values"], ["red", "blue"])
        self.assertEqual(f["tags"]["element"], "ref")

    def test_version_may_not_go_down(self):
        rg.update_lock(self.t.root)
        s = copy.deepcopy(MINI)
        s["version"] = "0.0.9"
        self.t.write(s)
        self.assertTrue(any("0.0.9" in e for e in self.errs()), self.errs())


class Outputs(unittest.TestCase):
    def setUp(self):
        self.t = Tree()
        rg.update_lock(self.t.root)
        self.out = rg.render(rg.load(self.t.root), rg.read_lock(self.t.root))

    def tearDown(self):
        self.t.close()

    def test_every_output_named(self):
        self.assertEqual(sorted(self.out), sorted([
            "native/services/content/registry/schema.g.h",
            "managed/services/Kiln.Registry/Generated/Things.gen.cs",
            "managed/services/Kiln.Registry/Generated/RegistrySchema.gen.cs",
            "schema/registry-section.schema.json",
            "docs/kiln/modding/registries/things.md",
            "managed/Kiln.Analyzers/src/Kiln.Analyzers/RegistryHints.g.cs",
        ]))

    def test_deterministic(self):
        again = rg.render(rg.load(self.t.root), rg.read_lock(self.t.root))
        self.assertEqual(self.out, again)

    def test_native_ids_follow_the_lock(self):
        h = self.out["native/services/content/registry/schema.g.h"]
        self.assertIn("things_size = 0x00010001u", h)
        self.assertIn("things_fixed = 0x00010009u", h)
        self.assertIn('"rows[].inner.x"', h)
        self.assertIn("Applies::WorldLoad", h)

    def test_csharp_carries_applies_on_every_leaf(self):
        cs = self.out["managed/services/Kiln.Registry/Generated/Things.gen.cs"]
        for path, applies in [("size", "WorldLoad"), ("color", "Now"), ("parts.a", "Restart"), ("parts.b", "Now"),
                              ("tags", "Now"), ("rows", "Now"), ("rows[].n", "Now"), ("rows[].inner.x", "Now"), ("fixed", "Restart")]:
            self.assertEqual(cs.count(f'[RegistryField("things.{path}", RegistryApplies.{applies})]'), 1, path)
        self.assertIn('CodeAnalysis.Experimental("KILNX031"', cs)
        self.assertIn("public sealed partial class ThingEntry", cs)
        self.assertIn("public sealed partial class ThingRow", cs)
        self.assertIn("public int Fixed => ", cs)  # read-only: no setter
        self.assertIn("public RegistryValueList<RegistryId> Tags =>", cs)
        self.assertIn("public RegistryEntryList<ThingRow> Rows =>", cs)

    def test_section_schema_names_the_fields(self):
        s = json.loads(self.out["schema/registry-section.schema.json"])
        things = s["$defs"]["things"]
        self.assertEqual(sorted(things["$defs"]["field"]["enum"]), sorted(["size", "color", "parts.a", "parts.b", "tags", "rows", "rows[].n", "rows[].inner.x"]))

    def test_docs_show_applies(self):
        md = self.out["docs/kiln/modding/registries/things.md"]
        self.assertIn("| `size` | int 1..10 | worldLoad |", md)
        self.assertIn("Do not edit", md)
        self.assertIn("are reserved", md)
        self.assertIn("\x60game\x60 and \x60kiln\x60", md)

    def test_section_add_ids(self):
        s = json.loads(self.out["schema/registry-section.schema.json"])
        pat = re.compile(s["$defs"]["things"]["$defs"]["edit"]["properties"]["id"]["pattern"])
        self.assertTrue(pat.match("mymod:crate"))
        for bad in ("game:crate", "kiln:crate", "MyMod:crate", "mymod:Crate", "mymod:", "mymod:a b"):
            self.assertFalse(pat.match(bad), bad)

    def test_docs_struct_doc_and_mapping(self):
        t = Tree(MINI)
        try:
            s = copy.deepcopy(MINI)
            s["fields"]["parts"]["doc"] = "The parts of a thing."
            t.write(s)
            rg.update_lock(t.root)
            (t.root / "regmap" / "common").mkdir(parents=True)
            (t.root / "regmap" / "common" / "things.json").write_text(json.dumps({"regmap": 1, "registry": "things",
                "entries": {"kind": "resource", "resourceType": "keen::Thing"},
                "rows": {"size": {"paths": ["a"], "conv": "int", "read": "worldLoad", "backend": "live"},
                         "color": {"paths": ["b"], "conv": "enum", "read": "unknown", "backend": "live", "state": "unproven", "reason": "when the game reads the color is not traced"}}}))
            md = rg.render(rg.load(t.root), rg.read_lock(t.root))["docs/kiln/modding/registries/things.md"]
            self.assertIn("The parts of a thing.", md)
            self.assertIn("| Mapping |", md)
            self.assertIn("unproven: when the game reads the color is not traced", md)
            self.assertIn("| \x60size\x60 | int 1..10 | worldLoad | number | 0.1.0 | mapped |", md)
            self.assertIn("not mapped yet", md)       # fields without a row
        finally:
            t.close()

    def test_native_carries_the_migrations(self):
        """A synthetic breaking step 0.1.0 -> 0.2.0 (test fixture only): kiln.content gets its renames in schema.g.h."""
        t = Tree()
        try:
            rg.update_lock(t.root)
            s = copy.deepcopy(MINI)
            s["version"] = "0.2.0"
            s["fields"]["length"] = s["fields"].pop("size")
            t.write(s)
            rg.retire(t.root, "things.size", "0.2.0")
            migration(t, "0.1.0", "0.2.0", ids={"game:old_thing": "game:new_thing"}, fields={"size": "length"})
            rg.update_lock(t.root)
            h = rg.render(rg.load(t.root), rg.read_lock(t.root))["native/services/content/registry/schema.g.h"]
            self.assertIn('{"things", "0.1.0", "0.2.0", true, "size", "length"},', h)
            self.assertIn('{"things", "0.1.0", "0.2.0", false, "game:old_thing", "game:new_thing"},', h)
            self.assertLess(h.index('"size", "length"'), h.index("{nullptr, nullptr, nullptr, false, nullptr, nullptr}"), "rows before the end mark")
        finally:
            t.close()

    def test_no_migration_no_rows(self):
        h = self.out["native/services/content/registry/schema.g.h"]
        self.assertIn("inline constexpr Rename kRenames[] = {\n    {nullptr, nullptr, nullptr, false, nullptr, nullptr},\n};", h)

    def test_check_finds_stale_files(self):
        rg.write_outputs(self.t.root, self.out)
        self.assertEqual(rg.stale(self.t.root, self.out), [])
        p = self.t.root / "docs/kiln/modding/registries/things.md"
        p.write_text(p.read_text() + "edited\n")
        self.assertEqual(rg.stale(self.t.root, self.out), ["docs/kiln/modding/registries/things.md"])


class Repository(unittest.TestCase):
    def test_the_repository_is_current(self):
        """registrygen.py --check on this tree: the schemas are valid, every name is locked, the outputs are fresh."""
        self.assertEqual(rg.main(["--root", str(REPO), "--check"]), 0)

    def test_the_section_schema_has_no_empty_enum(self):
        """A registry whose every field is read-only (stations) takes no edit's field: the section schema says so with a schema
        that matches nothing, never "enum": [] (JSON Schema wants at least one value; ajv refuses to compile the whole schema,
        so kiln-manage's lint test ajv_agrees failed on the merged dev.32 tree)."""
        s = json.loads((REPO / rg.SECTION).read_text(encoding="utf-8"))

        def empties(o, at):
            if isinstance(o, dict):
                if o.get("enum") == []:
                    yield at
                for k, v in o.items():
                    yield from empties(v, f"{at}/{k}")
            elif isinstance(o, list):
                for i, v in enumerate(o):
                    yield from empties(v, f"{at}[{i}]")
        self.assertEqual(list(empties(s, "")), [])
        self.assertEqual(s["$defs"]["stations"]["$defs"]["field"].get("not"), {})

    def test_r1_registries(self):
        m = rg.load(REPO)
        self.assertEqual(m.errors, [])
        # R1's five and dev.32's; later waves add registries of their own (each pinned by its own test below)
        self.assertLessEqual({"balance", "drops", "items", "loot", "recipes", "skills", "stations", "buffs", "abilities", "world", "difficulty",
                              "limits", "lootRules", "randomLoot"}, set(m.registries))
        for r in m.registries.values():
            for f in r.rows:
                self.assertIn(f.applies, ("now", "worldLoad", "restart"), f.qualified)
                self.assertIn(f.spec.get("joinedClient"), rg.JOINED_CLIENT, f.qualified)
        # experimental 0.x; items 0.1.1 since 31.x (block and comfort apply now: an earlier applies, additive); balance 0.1.1 since
        # dev.32 (altarsPerFlameLevel up to 255 with limits.maxAltars: a wider range, additive)
        self.assertEqual({n: r.version for n, r in m.registries.items()
                          if n in ("balance", "drops", "items", "loot", "recipes", "skills", "stations", "buffs", "abilities", "world", "difficulty",
                                   "limits", "lootRules", "randomLoot")},
                         {"balance": "0.1.1", "drops": "0.1.0", "items": "0.1.1", "loot": "0.1.0", "recipes": "0.1.0", "skills": "0.1.0",
                          "stations": "0.1.0", "buffs": "0.1.0", "abilities": "0.1.0", "world": "0.1.0", "difficulty": "0.1.0", "limits": "0.1.0",
                          "lootRules": "0.1.0", "randomLoot": "0.1.0"})

    def test_dev32_creatures_arsenals_and_perks(self):
        """dev.32 R2a: creatures (with their arsenals) and perks, experimental 0.1.0; pets and taming read a creature's kind,
        level and faction, so kind is a read-only field and every leaf names when a joined client sees it."""
        m = rg.load(REPO)
        self.assertEqual(m.errors, [])
        for name in ("creatures", "arsenals", "perks"):
            self.assertIn(name, m.registries)
            r = m.registries[name]
            # creatures 0.1.1: the taming fields added within the 0.1 line (dev.32 TAME review)
            self.assertEqual((r.version, r.doc["stability"], r.doc["experimentalId"]), ("0.1.1" if name == "creatures" else "0.1.0", "experimental", "KILNX031"))
            self.assertEqual(r.doc["entry"], {"add": False, "remove": False})
        rows = {f.qualified: f for r in m.registries.values() for f in r.rows}
        self.assertTrue(rows["creatures.kind"].read_only)
        self.assertEqual(rows["creatures.kind"].spec["values"], ["enemy", "wildlife", "animal", "villager"])
        self.assertTrue(rows["creatures.arsenal"].read_only)
        # a wild animal's taming: tameable and its favourite bait read only, its steps live (dev.32 TAME review)
        self.assertTrue(rows["creatures.tameable"].read_only and rows["creatures.favoriteBait"].read_only)
        self.assertFalse(rows["creatures.tamingSteps"].read_only)
        self.assertEqual(rows["creatures.favoriteBait"].spec["registry"], "templates")
        self.assertEqual(rows["creatures.arsenal"].spec["registry"], "arsenals")
        self.assertFalse(rows["creatures.level"].read_only)
        self.assertFalse(rows["creatures.faction"].read_only)
        self.assertEqual(rows["arsenals.attacks"].ops, "list")
        self.assertEqual(rows["arsenals.attacks[].chance"].list_path, "attacks")
        # the perk damage bonus: its read time is not traced, so it ships the cautious applies (never now)
        self.assertEqual(rows["perks.damage.cut"].applies, "worldLoad")
        self.assertEqual(rows["perks.value"].applies, "now")
        self.assertEqual(rows["perks.value"].spec.get("min"), -100000)

    def test_dev32_buffs_and_abilities(self):
        """dev.32's gamedata registries: experimental (KILNX031) 0.1.0, no raw paths, no entry add or remove, every field now."""
        m = rg.load(REPO)
        for name in ("buffs", "abilities"):
            r = m.registries[name]
            self.assertEqual((r.version, r.doc["stability"], r.doc["experimentalId"], r.doc["raw"]), ("0.1.0", "experimental", "KILNX031", "refused"))
            self.assertEqual((r.doc["entry"]["add"], r.doc["entry"]["remove"]), (False, False))
            self.assertTrue(all(f.applies == "now" for f in r.rows))
        self.assertEqual(sorted(f.path for f in m.registries["abilities"].rows),
                         ["timing.buff", "timing.cost", "timing.hit", "timing.release"])
        self.assertEqual({f.path: f.read_only for f in m.registries["buffs"].rows},
                         {"duration": False, "stacking": False, "kind": True, "saved": True})


if __name__ == "__main__":
    unittest.main(verbosity=1)
