# SPDX-License-Identifier: MIT
"""Tests for report.py, and the coverage of the client pass (rows.json against the check pack's sources).

    python3 -m unittest discover -s tools/client-checks
"""
from __future__ import annotations

import contextlib
import io
import json
import os
import re
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import report  # noqa: E402

REPO = HERE.parent.parent
PACK = REPO / "mods" / "tests" / "client-pass"


def check(mod: str, cid: str, area: str, title: str = "t") -> dict:
    return {"mod": mod, "id": cid, "title": title, "area": area, "since": "0.2.0-dev.25", "tags": [], "needs": "any",
            "order": 0}


def entry(mod: str, cid: str, area: str, verdict: str, note: str = "", assertions: list | None = None,
          time: str = "2026-11-12T01:00:00Z") -> dict:
    return {"mod": mod, "modVersion": "0.1.0", "id": cid, "title": "t", "area": area, "since": "0.2.0-dev.25",
            "tags": [], "kiln": "0.2.0-dev.25", "game": {"side": "client", "sha256": None, "verified": True},
            "session": "hosted", "verdict": verdict, "note": note, "time": time, "assertions": assertions or [], "log": []}


def sample_ledger() -> dict:
    return {
        "format": 1,
        "checks": [
            check("client-pass", "a.one", "Alpha"),
            check("client-pass", "a.two", "Alpha"),
            check("client-pass", "a.three", "Alpha"),
            check("client-pass", "b.one", "Beta"),
            check("client-pass", "b.never", "Beta"),
            check("other", "c.one", "Gamma"),
        ],
        "entries": [
            entry("client-pass", "a.one", "Alpha", "fail", "first try"),
            entry("client-pass", "a.two", "Alpha", "skip"),
            entry("client-pass", "a.one", "Alpha", "pass"),             # newer: the pass counts
            entry("client-pass", "a.three", "Alpha", "pass",
                  assertions=[{"what": "three hits counted", "ok": False, "detail": "counted 2"}]),
            entry("client-pass", "b.one", "Beta", "pass"),
            entry("client-pass", "b.one", "Beta", "fail", "no flash on the second hit"),  # newer: the fail counts
            entry("other", "c.one", "Gamma", "skip"),
        ],
    }


class SummaryTests(unittest.TestCase):
    def test_counts_per_area(self):
        r = report.summarize(sample_ledger())
        self.assertEqual(r["areas"]["Alpha"], {"passed": 2, "failed": 0, "skipped": 1, "never run": 0,
                                               "passedWithFailedAssertions": 1})
        self.assertEqual(r["areas"]["Beta"], {"passed": 0, "failed": 1, "skipped": 0, "never run": 1,
                                              "passedWithFailedAssertions": 0})
        self.assertEqual(r["areas"]["Gamma"]["skipped"], 1)
        self.assertEqual(r["totals"], {"passed": 2, "failed": 1, "skipped": 2, "never run": 1,
                                       "passedWithFailedAssertions": 1})

    def test_newest_entry_wins(self):
        r = report.summarize(sample_ledger())
        self.assertEqual([f["id"] for f in r["failed"]], ["b.one"])
        self.assertEqual(r["failed"][0]["note"], "no flash on the second hit")

    def test_never_run_comes_from_the_catalogue(self):
        ledger = sample_ledger()
        ledger["checks"].append(check("client-pass", "z.new", "Zeta"))
        r = report.summarize(ledger)
        self.assertEqual(r["areas"]["Zeta"]["never run"], 1)

    def test_entry_without_catalogue_line_still_counts(self):
        ledger = sample_ledger()
        ledger["entries"].append(entry("late", "d.one", "Delta", "pass"))
        r = report.summarize(ledger)
        self.assertEqual(r["areas"]["Delta"]["passed"], 1)

    def test_pass_with_failed_assertion_is_marked(self):
        r = report.summarize(sample_ledger())
        self.assertEqual([m["id"] for m in r["marked"]], ["a.three"])
        text = report.render(r)
        self.assertIn("passed with a failed assertion (*): 1", text)
        self.assertIn("passed* client-pass/a.three: three hits counted (counted 2)", text)

    def test_other_format_is_refused(self):
        ledger = sample_ledger()
        ledger["format"] = 2
        with self.assertRaises(report.LedgerError) as e:
            report.summarize(ledger)
        self.assertIn("format 2", str(e.exception))
        self.assertIn("format 1 only", str(e.exception))

    def test_rows_state(self):
        rows = [{"section": "S", "key": "row one", "checks": ["a.one", "a.three"]},
                {"section": "S", "key": "row two", "checks": ["a.one", "b.one"]},
                {"section": "S", "key": "row three", "checks": ["a.two", "other/c.one"]},
                {"section": "S", "key": "row four", "checks": ["missing.id"]}]
        r = report.summarize(sample_ledger(), rows)
        self.assertEqual([x["state"] for x in r["rows"]], ["passed", "failed", "open", "open"])
        self.assertTrue(r["rows"][0]["checks"][1]["marked"])
        self.assertFalse(r["rows"][3]["checks"][0]["known"])
        self.assertIn("missing.id never run (not in the ledger)", report.render(r))


class CommandLineTests(unittest.TestCase):
    def run_main(self, *args: str) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            rc = report.main(list(args))
        return rc, out.getvalue(), err.getvalue()

    def test_reads_a_ledger_and_prints_json(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "client-checks.json")
            with open(path, "w", encoding="utf-8") as f:
                json.dump(sample_ledger(), f)
            rc, out, _ = self.run_main(path)
            self.assertEqual(rc, 0)
            self.assertIn("Alpha", out)
            rc, out, _ = self.run_main(path, "--json", "--rows", str(PACK / "rows.json"))
            self.assertEqual(rc, 0)
            data = json.loads(out)
            self.assertEqual(data["totals"]["failed"], 1)
            self.assertEqual(len(data["rows"]), len(json.loads((PACK / "rows.json").read_text("utf-8"))["rows"]))

    def test_unreadable_ledger_exits_2(self):
        with tempfile.TemporaryDirectory() as d:
            rc, _, err = self.run_main(os.path.join(d, "missing.json"))
            self.assertEqual(rc, 2)
            self.assertIn("cannot read the ledger", err)
            bad = os.path.join(d, "bad.json")
            with open(bad, "w", encoding="utf-8") as f:
                f.write("{ not json")
            rc, _, err = self.run_main(bad)
            self.assertEqual(rc, 2)
            self.assertIn("not valid JSON", err)

    def test_other_format_exits_2_with_a_message(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "client-checks.json")
            ledger = sample_ledger()
            ledger["format"] = 7
            with open(path, "w", encoding="utf-8") as f:
                json.dump(ledger, f)
            rc, out, err = self.run_main(path)
            self.assertEqual(rc, 2)
            self.assertEqual(out, "")
            self.assertIn("format 7", err)


# [ClientCheck("id", ...)] in the pack's sources.
CHECK_ID = re.compile(r'\[ClientCheck\(\s*"([^"]+)"')


def pack_check_ids() -> list[str]:
    ids = []
    for src in sorted(PACK.glob("*.cs")):
        ids += CHECK_ID.findall(src.read_text("utf-8"))
    return ids


def coverage_problems(rows: dict, ids: list[str]) -> list[str]:
    """What is wrong with the mapping: a row with no check, a mapped id that is no check, two checks with one id."""
    problems = []
    seen = set()
    for cid in ids:
        if cid in seen:
            problems.append(f"two checks share the id {cid}")
        seen.add(cid)
    for row in rows["rows"]:
        if not row.get("checks"):
            problems.append(f"row without a check: {row.get('section')}: {row.get('key')}")
        for cid in row.get("checks", []):
            if "/" not in cid and cid not in seen:
                problems.append(f"row {row.get('key')!r} names {cid}, which is no check in the pack")
            elif "/" in cid:
                mod, _, other = cid.rpartition("/")
                if other not in mod_check_ids(mod):
                    problems.append(f"row {row.get('key')!r} names {cid}, which is no check of the mod {mod}")
    return problems


def relaxed_json(text: str):
    """A kiln.mod.json as Kiln reads it: `//` comments and trailing commas are allowed (docs/kiln/modding/manifest.md)."""
    out, i, n, in_string = [], 0, len(text), False
    while i < n:
        c = text[i]
        if in_string:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1])
                i += 1
            elif c == '"':
                in_string = False
        elif c == '"':
            in_string = True
            out.append(c)
        elif text.startswith("//", i):
            while i < n and text[i] != "\n":
                i += 1
            continue
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            i = n if end < 0 else end + 2
            continue
        elif c in "}]":
            # a comma before a closing bracket, with only white space between (comments are gone by now), is dropped
            k = len(out) - 1
            while k >= 0 and out[k].isspace():
                k -= 1
            if k >= 0 and out[k] == ",":
                del out[k]
            out.append(c)
        else:
            out.append(c)
        i += 1
    return json.loads("".join(out))


def mod_check_ids(mod: str) -> set[str]:
    """The check ids in the sources of the mod (under mods/) whose kiln.mod.json has this id; empty when none has."""
    for manifest in sorted((PACK.parent.parent).glob("**/kiln.mod.json")):
        try:
            if relaxed_json(manifest.read_text("utf-8")).get("id") != mod:
                continue
        except (ValueError, OSError):
            continue
        return {cid for src in manifest.parent.rglob("*.cs") for cid in CHECK_ID.findall(src.read_text("utf-8"))}
    return set()


class RelaxedJsonTests(unittest.TestCase):
    def test_a_manifest_with_comments_and_a_trailing_comma_is_read(self):
        text = '{ "id": "a", // why\n "url": "https://example.org/x", /* note */ "writes": ["Fuel", ], }'
        self.assertEqual(relaxed_json(text), {"id": "a", "url": "https://example.org/x", "writes": ["Fuel"]})

    def test_a_comment_mark_inside_a_string_stays(self):
        self.assertEqual(relaxed_json('{ "d": "a // b \\" c, }" }'), {"d": 'a // b " c, }'})


class CoverageTests(unittest.TestCase):
    """The client pass covers every line of the status page it was written for."""

    @classmethod
    def setUpClass(cls):
        cls.rows = json.loads((PACK / "rows.json").read_text("utf-8"))
        cls.ids = pack_check_ids()

    def test_every_row_has_a_check_and_every_id_exists_once(self):
        self.assertEqual(coverage_problems(self.rows, self.ids), [])

    def test_the_status_page_counts(self):
        count = {}
        for row in self.rows["rows"]:
            count[row["section"]] = count.get(row["section"], 0) + 1
        self.assertEqual(count.get("Built, game test pending"), 18)
        self.assertEqual(count.get("Client checklist (dev.22)"), 11)
        self.assertEqual(count.get("Client checklist (dev.21)"), 11)
        keys = [(r["section"], r["key"]) for r in self.rows["rows"]]
        self.assertEqual(len(keys), len(set(keys)), "a row is listed twice")

    def test_ids_follow_the_rule(self):
        for cid in self.ids:
            self.assertRegex(cid, r"^[a-z0-9][a-z0-9._-]{0,63}$")
            self.assertIn(".", cid, f"{cid}: ids are area.slug")

    def test_the_coverage_check_can_fail(self):
        rows = {"rows": [{"section": "S", "key": "empty", "checks": []},
                         {"section": "S", "key": "ghost", "checks": ["no.such-check"]}]}
        problems = coverage_problems(rows, ["a.one", "a.one"])
        self.assertEqual(len(problems), 3, problems)


if __name__ == "__main__":
    unittest.main()
