# SPDX-License-Identifier: MIT
"""build_record tests: record format 2 on a synthetic exe and sigdb, --refresh, apply, check, and check on the
repository's own records (a sigdb name added without a refresh turns this red).

    python3 -m unittest discover -s tools/sigport/tests -v
"""
import copy
import io
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))

import build_record as br  # noqa: E402
from test_sigport import DEFS, build_pe, caller, filler, target  # noqa: E402

# A .d file: a symbol on the caller whose pattern captures a symbol and a constant nobody defines as a key, a probe,
# and a feature on the captured symbol.
EXTRA = {
    "format": 1, "side": "server", "comment": "test fragment",
    "symbols": {
        "extra/CallSite": {"find": [{"pattern": "B8 {extra/answer:d32} 89 44 24 20 E8 {extra/Target:rel32}"}]},
        "@extra/CallSite.fields": {"find": [{"scope": {"symbol": "extra/CallSite", "size": 32},
                                             "pattern": "89 44 24 20 E8"}]},
    },
    "features": {"extra.target": {"symbols": ["extra/Target"], "constants": ["extra/answer"]}},
}


def run(argv):
    out = io.StringIO()
    with redirect_stdout(out):
        rc = br.main(argv)
    return rc, out.getvalue()


class Synthetic(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        d = self.d = Path(self.tmp.name)
        self.symbols = d / "symbols"
        self.builds = d / "builds"
        (self.symbols / "server.d").mkdir(parents=True)
        (self.symbols / "server.json").write_text(json.dumps(DEFS, indent=2) + "\n")
        self.write_extra(EXTRA)
        self.exe = d / "server.exe"
        build_pe(self.exe, [(filler(1), True), (target(0xE15DC0, 0x20, field_disp8=0x10), True), (filler(2), True),
                            (caller(1), True)])
        self.common = ["--symbols", str(self.symbols), "--builds", str(self.builds)]

    def tearDown(self):
        self.tmp.cleanup()

    def write_extra(self, doc):
        (self.symbols / "server.d" / "extra.json").write_text(json.dumps(doc, indent=2) + "\n")

    def new_record(self, *extra):
        rc, out = run(["server", str(self.exe), "--steam-build", "100"] + self.common + list(extra))
        self.assertEqual(rc, 0, out)
        self.path = next((self.builds / "server").glob("*.json"))
        return json.loads(self.path.read_text())

    def check(self):
        return run(["check", "server"] + self.common)

    def lab_result(self, rec, services, failed=0):
        """A result file as collect writes it, with the values the core would report for this record."""
        values = {"symbols": {}, "constants": {}, "features": {}}
        for e in rec["services"].values():
            for section in br.SECTIONS:
                for name, level in e[section].items():
                    ok = level != "unresolved"
                    if section == "features" or name.startswith("@"):
                        values[section][name] = ok
                    else:
                        values[section][name] = rec[section][name] if ok else False
        return {"result": 1, "kind": "lab", "side": "server", "sha256": rec["sha256"], "kiln": "0.0.0-test",
                "date": "2026-01-02", "boot": "b1", "lab": "mylab", "policy": "any", "boot_complete": True,
                "core_errors": [], "values": values,
                "services": {s: {"loaded": True, "loaded_line": "x",
                                 "tests": {f"{s}-test": {"passed": 5, "failed": failed, "skipped": 0,
                                                         "finished": True, "fails": ["FAIL a: b"] if failed else []}}}
                             for s in services}}

    def apply(self, *results):
        files = []
        for i, r in enumerate(results):
            p = self.d / f"result{i}.json"
            p.write_text(json.dumps(r))
            files.append(str(p))
        return run(["apply"] + files + ["--record", str(self.path)])

    def test_fresh_record_lists_every_declared_name(self):
        rec = self.new_record()
        self.assertEqual(rec["record"], 2)
        self.assertFalse(rec["verified"])
        core, extra = rec["services"]["core"], rec["services"]["extra"]
        self.assertEqual(set(core["symbols"]), {"GetThing", "@GetThing.fields", "Caller"})
        self.assertEqual(set(core["constants"]), {"world.storage", "thing.field"})
        # captured-only names belong to the file whose pattern captures them; the probe is listed too
        self.assertEqual(set(extra["symbols"]), {"extra/CallSite", "@extra/CallSite.fields", "extra/Target"})
        self.assertEqual(set(extra["constants"]), {"extra/answer"})
        self.assertEqual(set(extra["features"]), {"extra.target"})
        self.assertEqual(rec["declared"], {"symbols": 6, "constants": 3, "features": 2})
        for e in rec["services"].values():
            self.assertEqual(e["level"], "static")
            self.assertTrue(all(v == "static" for s in br.SECTIONS for v in e[s].values()))
        self.assertEqual(rec["constants"]["extra/answer"], "0x2a")
        self.assertEqual(rec["symbols"]["extra/Target"], rec["symbols"]["GetThing"])
        rc, out = self.check()
        self.assertEqual(rc, 0, out)
        self.assertIn("6 of 6 symbols listed (5 defined, 1 captured)", out)

    def test_unresolved_name_has_its_reason(self):
        doc = copy.deepcopy(EXTRA)
        doc["symbols"]["extra/Missing"] = {"find": [{"pattern": "DE AD BE EF 01 02"}]}
        self.write_extra(doc)
        rec = self.new_record()
        e = rec["services"]["extra"]
        self.assertEqual(e["symbols"]["extra/Missing"], "unresolved")
        self.assertIn("0 matches", e["unresolved"]["extra/Missing"])
        self.assertEqual(self.check()[0], 0)

    def test_refresh_keeps_levels_and_adds_new_names_at_static(self):
        rec = self.new_record()
        rc, out = self.apply(self.lab_result(rec, ["core", "extra"]))
        self.assertEqual(rc, 0, out)
        rec = json.loads(self.path.read_text())
        self.assertEqual(rec["services"]["extra"]["level"], "lab")
        self.assertEqual(rec["services"]["extra"]["symbols"]["extra/Target"], "lab")
        evidence = rec["services"]["extra"]["evidence"]
        self.assertIn("extra-test 5/0", evidence)
        doc = copy.deepcopy(EXTRA)
        doc["features"]["extra.caller"] = {"symbols": ["Caller"]}
        self.write_extra(doc)
        rc, out = self.check()
        self.assertEqual(rc, 1)
        self.assertIn("missing: feature extra.caller (declared by extra)", out)
        rc, out = run(["server", str(self.exe), "--refresh"] + self.common)
        self.assertEqual(rc, 0, out)
        self.assertIn("added    extra: feature extra.caller (static)", out)
        new = json.loads(self.path.read_text())
        self.assertEqual(new["services"]["extra"]["features"]["extra.caller"], "static")
        self.assertEqual(new["services"]["extra"]["features"]["extra.target"], "lab")
        self.assertEqual(new["services"]["extra"]["level"], "lab")
        self.assertEqual(new["services"]["extra"]["evidence"], evidence)
        self.assertEqual(new["services"]["core"]["symbols"]["GetThing"], "lab")
        self.assertEqual(new["declared"]["features"], 3)
        self.assertEqual(self.check()[0], 0)

    def test_refresh_twice_is_byte_identical_and_keeps_verified_and_note(self):
        self.new_record("--verified", "--note", "ran things")
        self.apply(self.lab_result(json.loads(self.path.read_text()), ["core"]))
        run(["server", str(self.exe), "--refresh"] + self.common)
        first = self.path.read_bytes()
        rc, out = run(["server", str(self.exe), "--refresh"] + self.common)
        self.assertEqual(rc, 0, out)
        self.assertIn("unchanged", out)
        self.assertEqual(self.path.read_bytes(), first)
        rec = json.loads(first)
        self.assertTrue(rec["verified"])
        self.assertEqual(rec["note"], "ran things")
        self.assertEqual(rec["services"]["core"]["level"], "lab")

    def test_refresh_drops_names_sigdb_no_longer_declares(self):
        self.new_record()
        doc = copy.deepcopy(EXTRA)
        del doc["symbols"]["@extra/CallSite.fields"]
        self.write_extra(doc)
        rc, out = run(["server", str(self.exe), "--refresh"] + self.common)
        self.assertEqual(rc, 0, out)
        self.assertIn("dropped  extra: symbol @extra/CallSite.fields", out)
        self.assertNotIn("@extra/CallSite.fields", json.loads(self.path.read_text())["services"]["extra"]["symbols"])

    def test_refresh_refuses_when_a_lab_name_changed(self):
        rec = self.new_record()
        self.apply(self.lab_result(rec, ["extra"]))
        before = self.path.read_bytes()
        doc = copy.deepcopy(EXTRA)
        doc["symbols"]["extra/CallSite"]["find"][0]["pattern"] = "DE AD BE EF 01 02"   # now unresolved
        self.write_extra(doc)
        rc, out = run(["server", str(self.exe), "--refresh"] + self.common)
        self.assertEqual(rc, 1)
        self.assertIn("changed  extra: symbol extra/CallSite: was lab, now unresolved", out)
        self.assertEqual(self.path.read_bytes(), before)
        rc, out = run(["server", str(self.exe), "--refresh", "--demote-changed"] + self.common)
        self.assertEqual(rc, 0, out)
        e = json.loads(self.path.read_text())["services"]["extra"]
        self.assertEqual(e["symbols"]["extra/CallSite"], "unresolved")

    def test_plain_write_refuses_to_drop_lab_levels(self):
        rec = self.new_record()
        self.apply(self.lab_result(rec, ["core"]))
        with redirect_stdout(io.StringIO()), self.assertRaises(SystemExit):
            from contextlib import redirect_stderr
            with redirect_stderr(io.StringIO()):
                br.main(["server", str(self.exe)] + self.common)

    def test_a_failing_test_mod_leaves_the_service_static(self):
        rec = self.new_record()
        rc, out = self.apply(self.lab_result(rec, ["extra"], failed=1))
        self.assertEqual(rc, 1)
        self.assertIn("extra: stays static", out)
        self.assertIn("extra-test 5/1: FAIL a: b", out)
        self.assertEqual(json.loads(self.path.read_text())["services"]["extra"]["level"], "static")

    def test_a_value_the_core_resolved_differently_leaves_the_service_static(self):
        rec = self.new_record()
        res = self.lab_result(rec, ["core", "extra"])
        res["values"]["constants"]["extra/answer"] = "0x2b"
        rc, out = self.apply(res)
        self.assertEqual(rc, 1)
        self.assertIn("constant extra/answer: core 0x2b, offline 0x2a", out)
        rec = json.loads(self.path.read_text())
        self.assertEqual(rec["services"]["extra"]["level"], "static")
        self.assertEqual(rec["services"]["core"]["level"], "lab")

    def test_carry_raises_only_names_with_the_same_value(self):
        rec = self.new_record()
        old = {"sha256": rec["sha256"], "side": "server", "symbols": {"GetThing": rec["symbols"]["GetThing"],
                                                                      "Caller": "0x1"},
               "constants": {"world.storage": rec["constants"]["world.storage"]}, "features": {"thing": True}}
        res = br.carry(old, "2025-12-31", "an earlier boot")
        rc, out = self.apply(res)
        self.assertEqual(rc, 0, out)
        core = json.loads(self.path.read_text())["services"]["core"]
        self.assertEqual(core["level"], "lab")
        self.assertEqual(core["date"], "2025-12-31")
        self.assertEqual(core["symbols"]["GetThing"], "lab")
        self.assertEqual(core["symbols"]["Caller"], "static")            # value differs
        self.assertEqual(core["symbols"]["@GetThing.fields"], "static")  # probes are not in the old maps
        self.assertEqual(core["features"]["thing"], "lab")

    def test_bad_level_fails_check(self):
        self.new_record()
        rec = json.loads(self.path.read_text())
        rec["services"]["extra"]["symbols"]["extra/Target"] = "tested"
        self.path.write_text(json.dumps(rec, indent=2) + "\n")
        rc, out = self.check()
        self.assertEqual(rc, 1)
        self.assertIn("extra/Target: level 'tested' is not one of", out)

    def test_a_name_listed_twice_fails_check(self):
        self.new_record()
        rec = json.loads(self.path.read_text())
        rec["services"]["core"]["symbols"]["extra/Target"] = "static"
        self.path.write_text(json.dumps(rec, indent=2) + "\n")
        rc, out = self.check()
        self.assertEqual(rc, 1)
        self.assertIn("listed twice", out)

    def test_check_reads_only_the_newest_build(self):
        self.new_record()
        old = self.builds / "server" / ("0" * 64 + ".json")
        old.write_text(json.dumps({"sha256": "0" * 64, "steam_build": 5, "verified": True, "note": "",
                                   "features": {}, "symbols": {}, "constants": {}}))
        rc, out = self.check()
        self.assertEqual(rc, 0, out)
        self.assertIn("superseded", out)
        # a format-1 record as the newest build fails
        old.write_text(old.read_text().replace('"steam_build": 5', '"steam_build": 500'))
        rc, out = self.check()
        self.assertEqual(rc, 1)
        self.assertIn("record format 1", out)


class Collect(unittest.TestCase):
    def test_a_boot_folder_becomes_a_result(self):
        with tempfile.TemporaryDirectory() as tmp:
            k = Path(tmp) / "srv" / "kiln"
            (k / "logs").mkdir(parents=True)
            (k / "kiln.json").write_text('{"policy": "any"}')
            (k / "logs" / "sigdb-report.json").write_text(json.dumps({
                "kiln": "0.0.0-test", "exe_sha256": "ab" * 32,
                "symbols": {"S": {"ok": True, "rva": "0x10"}, "@S.fields": {"ok": True}, "T": {"ok": False}},
                "constants": {"c": {"ok": True, "value": "0x8"}}, "features": {"f": True}}))
            earlier = Path(tmp) / "boot1.log"
            earlier.write_text("[I 1] [mod:w-test] w-test: PASS one\n[I 2] [mod:w-test] READY stop\n")
            (k / "logs" / "kiln.log").write_text(
                "[I 0] [kiln] side: server (pdb enshrouded_server.pdb)\n"
                "[I 0] [kiln] service kiln.w v1 published by service:kiln-w.dll\n"
                "[I 0] [kiln] boot complete; handing control to the game\n"
                "[I 1] [mod:w-test] w-test: PASS two\n"
                "[I 1] [mod:w-test] w-test: done, 2 passed, 0 failed\n"
                "[I 1] [mod:x-test] x-test: PASS a\n"
                "[I 1] [mod:x-test] x-test: FAIL b: wrong\n"
                "[I 1] [mod:x-test] summary: 1 PASS, 1 FAIL\n")
            r = br.collect(k.parent, "w", {"w": ["w-test"], "core": ["x-test"], "gone": ["y-test"]}, "mylab",
                           earlier_logs=[earlier])
        self.assertEqual(r["values"]["symbols"], {"S": "0x10", "@S.fields": True, "T": False})
        self.assertTrue(r["boot_complete"])
        self.assertEqual(r["boots"], 2)
        w = r["services"]["w"]
        self.assertTrue(w["loaded"])
        self.assertEqual((w["tests"]["w-test"]["passed"], w["tests"]["w-test"]["failed"]), (2, 0))
        self.assertTrue(w["tests"]["w-test"]["finished"])
        x = r["services"]["core"]["tests"]["x-test"]
        self.assertEqual((x["passed"], x["failed"], x["fails"]), (1, 1, ["x-test: FAIL b: wrong"]))
        self.assertFalse(r["services"]["gone"]["loaded"])
        self.assertFalse(r["services"]["gone"]["tests"]["y-test"]["finished"])


class RepositoryRecords(unittest.TestCase):
    def test_newest_records_list_every_declared_name(self):
        """sigdb gained a name and the record did not: run build_record.py <side> <exe> --refresh."""
        ok, lines = br.check()
        self.assertTrue(ok, "\n".join(lines))


if __name__ == "__main__":
    unittest.main()
