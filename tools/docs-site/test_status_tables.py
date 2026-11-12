#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests for tools/docs-site/status-tables.py: grouping by area, the newest entry of a check wins, never-run checks,
pending rows, no client file, deterministic output, bad input (format, missing keys, wrong side) and that unknown extra
keys are ignored. Inputs are the hand-written files in tools/docs-site/fixtures/ and copies of them changed in memory.

  python3 -I tools/docs-site/test_status_tables.py      (or: cd tools/docs-site && python3 -I -m unittest)
Writes only under a temporary folder.
"""
import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
FIX = HERE / "fixtures"
spec = importlib.util.spec_from_file_location("status_tables", HERE / "status-tables.py")
st = importlib.util.module_from_spec(spec)
spec.loader.exec_module(st)


def fixture(name):
    return json.loads((FIX / name).read_text(encoding="utf-8"))


class Run:
    """Writes the given documents to a temporary folder and runs main() on them."""

    def __init__(self, server=None, client=None, checks=None, with_client=True):
        self.docs = {"server": server if server is not None else fixture("server-capabilities.json"),
                     "client": client if client is not None else fixture("client-capabilities.json"),
                     "checks": checks if checks is not None else fixture("client-checks.json")}
        self.with_client = with_client

    def __call__(self):
        with tempfile.TemporaryDirectory() as tmp:
            paths = {}
            for k, doc in self.docs.items():
                p = Path(tmp) / f"{k}.json"
                p.write_text(doc if isinstance(doc, str) else json.dumps(doc), encoding="utf-8")
                paths[k] = str(p)
            out = Path(tmp) / "status.generated.md"
            argv = ["--server", paths["server"], "--checks", paths["checks"], "-o", str(out)]
            if self.with_client:
                argv += ["--client", paths["client"]]
            err = io.StringIO()
            with contextlib.redirect_stderr(err):
                code = st.main(argv)
            text = out.read_text(encoding="utf-8") if out.exists() else None
            return code, text, err.getvalue()


def section(text, heading):
    """The lines of one '### heading' section."""
    lines = text.splitlines()
    start = lines.index(f"### {heading}") + 1
    end = next((i for i in range(start, len(lines)) if lines[i].startswith("#")), len(lines))
    return lines[start:end]


def row(text, area, key):
    hits = [l for l in section(text, area) if l.startswith(f"| `{key}` |") or f"| `{key}` |" in l]
    assert len(hits) == 1, (area, key, hits)
    return [c.strip() for c in hits[0].strip("|").split(" | ")]


class Grouping(unittest.TestCase):
    def test_rows_go_under_the_part_before_the_first_dot(self):
        code, text, _ = Run()()
        self.assertEqual(code, 0)
        self.assertEqual([l for l in text.splitlines() if l.startswith("### ")][:4],
                         ["### items", "### kiln", "### net", "### render"])
        self.assertIn("| `items.fuel` |", "\n".join(section(text, "items")))
        self.assertIn("| `items.moves` |", "\n".join(section(text, "items")))
        self.assertNotIn("items.", "\n".join(section(text, "net")))
        self.assertIn("| net | 0 of 2 | 0 of 0 |", text)

    def test_a_name_with_two_dots_takes_the_first_part(self):
        server = fixture("server-capabilities.json")
        server["capabilities"].append({"name": "zeta.a.b", "state": "available", "reason": "none", "reasonText": "", "source": "x"})
        _, text, _ = Run(server=server)()
        self.assertIn("### zeta", text)
        self.assertNotIn("### zeta.a", text)

    def test_server_and_client_rows_are_joined_by_name(self):
        _, text, _ = Run()()
        self.assertEqual(row(text, "items", "items.fuel")[1:],
                         ["available", "unavailable", "client: safe idle on an unverified build"])
        self.assertEqual(row(text, "render", "render.post")[1:3], ["no row", "unavailable"])
        self.assertEqual(row(text, "kiln", "kiln.render")[3], "server: client only; client: safe idle on an unverified build")


class States(unittest.TestCase):
    def test_pending_is_shown_as_depends_on_the_session(self):
        _, text, _ = Run()()
        r = row(text, "items", "items.moves")
        self.assertEqual(r[1], "depends on the session")
        self.assertEqual(r[3], "server: no world is loaded yet")
        self.assertNotIn("| pending |", text)

    def test_an_unavailable_row_with_no_text_shows_its_reason_code(self):
        _, text, _ = Run()()
        self.assertEqual(row(text, "net", "net.lobby")[3], "server: other-14")

    def test_an_available_row_has_no_reason(self):
        _, text, _ = Run()()
        self.assertEqual(row(text, "kiln", "kiln.items")[3], "")

    def test_markdown_in_a_reason_is_escaped(self):
        _, text, _ = Run()()
        self.assertEqual(row(text, "net", "net.kick")[3], "server: symbol net/KickPlayer: find\\[0\\]: 0 matches")
        server = fixture("server-capabilities.json")
        server["capabilities"][4]["reasonText"] = "a | b\nc"
        _, text, _ = Run(server=server)()
        self.assertIn("| server: a \\| b c |", text)


class NoClientFile(unittest.TestCase):
    def test_the_client_column_says_no_client_file_and_never_guesses(self):
        code, text, _ = Run(with_client=False)()
        self.assertEqual(code, 0)
        for name in ("items.fuel", "items.moves"):
            self.assertEqual(row(text, "items", name)[2], "no client file")
        self.assertNotIn("render.post", text)  # a client-only row cannot appear without the client file
        self.assertIn("| items | 1 of 2 | no client file |", text)
        self.assertIn("- client: no client file", text)
        self.assertNotIn("--client", text)
        self.assertEqual(row(text, "items", "items.moves")[3], "no world is loaded yet")


class Checks(unittest.TestCase):
    def test_runner_self_tests_are_labelled_as_lab_scripts(self):
        checks = fixture("client-checks.json")
        for c in checks["checks"]:
            c["mod"] = "client-check-test"
        for e in checks["entries"]:
            e["mod"] = "client-check-test"
        _, text, _ = Run(checks=checks)()
        self.assertEqual(row(text, "Items", "demo.first")[0],
                         "client-check-test (lab script, not a player)")
        self.assertEqual(row(text, "World", "demo.never")[0],
                         "client-check-test (lab script, not a player)")
        self.assertEqual(row(Run()()[1], "Items", "demo.first")[0], "demo")

    def test_verdict_notes_are_preserved_and_escaped(self):
        checks = fixture("client-checks.json")
        checks["entries"][-1]["note"] = "flash was green | not blue\n<guard warning>"
        _, text, _ = Run(checks=checks)()
        self.assertIn("| Game build | Notes |", text)
        self.assertEqual(row(text, "Items", "demo.first")[-1],
                         "flash was green \\| not blue \\<guard warning\\>")

    def test_failed_assertions_are_reported_even_with_a_pass(self):
        checks = fixture("client-checks.json")
        checks["entries"][-1]["note"] = "guard warning"
        checks["entries"][-1]["assertions"] = [
            {"what": "one", "ok": True}, {"what": "two", "ok": False}, {"what": "three", "ok": False}]
        _, text, _ = Run(checks=checks)()
        r = row(text, "Items", "demo.first")
        self.assertEqual(r[3], "pass")
        self.assertEqual(r[-1], "guard warning; 2 of 3 assertions failed")
        checks["entries"][-1]["assertions"] = [{"what": "one", "ok": True}]
        _, text, _ = Run(checks=checks)()
        self.assertEqual(row(text, "Items", "demo.first")[-1], "guard warning")

    def test_the_newest_entry_of_a_check_wins(self):
        _, text, _ = Run()()
        r = row(text, "Items", "demo.first")
        self.assertEqual(r[3:], ["pass", "2026-11-11", "hosted", "`0.2.0-dev.25`", "client `222222222222`", ""])
        self.assertNotIn("| fail |", text)

    def test_the_newest_is_the_last_in_the_list(self):
        checks = fixture("client-checks.json")
        checks["entries"].reverse()  # now the fail of dev.24 is the last one appended
        _, text, _ = Run(checks=checks)()
        self.assertEqual(row(text, "Items", "demo.first")[3], "fail")

    def test_a_check_with_no_entry_is_never_run(self):
        _, text, _ = Run()()
        r = row(text, "World", "demo.never")
        self.assertEqual(r[2:], ["Never played", "never run", "", "", "", "", ""])

    def test_no_entries_at_all(self):
        checks = fixture("client-checks.json")
        checks["entries"] = []
        _, text, _ = Run(checks=checks)()
        self.assertEqual(text.count("| never run |"), 3)
        self.assertIn("3 in the catalogue, 0 verdicts\n", text)

    def test_an_unknown_game_build_says_so(self):
        _, text, _ = Run()()
        self.assertEqual(row(text, "Items", "demo.second")[7], "client `unknown`")


class Deterministic(unittest.TestCase):
    def test_same_inputs_give_the_same_bytes(self):
        a = Run()()[1]
        b = Run()()[1]
        self.assertEqual(a, b)

    def test_input_order_does_not_change_the_page(self):
        server = fixture("server-capabilities.json")
        server["capabilities"].reverse()
        checks = fixture("client-checks.json")
        checks["checks"].reverse()
        self.assertEqual(Run(server=server, checks=checks)()[1], Run()()[1])

    def test_no_pid_path_or_time_of_the_run(self):
        _, text, _ = Run()()
        self.assertNotIn("4242", text)  # the pid
        self.assertNotIn("/tmp", text)
        self.assertNotIn(tempfile.gettempdir(), text)
        self.assertIn("--server server.json --client client.json --checks checks.json -o docs/kiln/status.generated.md", text)
        self.assertNotIn("22:40", text)  # only the date of the file's 'written' is shown

    def test_the_header_says_available_is_not_works_in_the_game(self):
        _, text, _ = Run()()
        self.assertIn("It does not say the feature works in the game. A pass is a real-client statement only when a player gave it in the game client.", text)
        self.assertIn("game build `1111111111111111111111111111111111111111111111111111111111111111` (a verified build)", text)


class BadInput(unittest.TestCase):
    def assertRefused(self, run, *words):
        code, text, err = run()
        self.assertEqual(code, 2)
        self.assertIsNone(text)
        for w in words:
            self.assertIn(w, err)

    def test_a_wrong_format_stops_with_exit_2(self):
        for bad in (2, "1", True, None):
            server = fixture("server-capabilities.json")
            server["format"] = bad
            self.assertRefused(Run(server=server), "server capabilities file has format", "format 1 only")
        checks = fixture("client-checks.json")
        checks["format"] = 2
        self.assertRefused(Run(checks=checks), "check ledger has format 2")

    def test_missing_keys_stop_with_exit_2(self):
        server = fixture("server-capabilities.json")
        del server["capabilities"][1]["state"]
        self.assertRefused(Run(server=server), "capability 1 of the server capabilities file has no 'state'")
        server = fixture("server-capabilities.json")
        del server["exe"]
        self.assertRefused(Run(server=server), "has no 'exe'")
        checks = fixture("client-checks.json")
        del checks["entries"]
        self.assertRefused(Run(checks=checks), "has no 'entries'")
        checks = fixture("client-checks.json")
        del checks["entries"][0]["verdict"]
        self.assertRefused(Run(checks=checks), "entry 0 of the check ledger has no 'verdict'")
        checks = fixture("client-checks.json")
        del checks["checks"][2]["title"]
        self.assertRefused(Run(checks=checks), "check 2 of the check ledger has no 'title'")

    def test_not_json_and_wrong_side_stop_with_exit_2(self):
        self.assertRefused(Run(checks="{not json"), "is not valid JSON")
        self.assertRefused(Run(server=fixture("client-capabilities.json")), "from a 'client' boot; 'server' expected")
        server = fixture("server-capabilities.json")
        server["capabilities"][0]["state"] = "maybe"
        self.assertRefused(Run(server=server), "state 'maybe'")

    def test_unknown_extra_keys_are_ignored(self):
        server = fixture("server-capabilities.json")
        self.assertIn("futureKey", server)  # the fixture carries extra keys at top level and on a row
        server["capabilities"][0]["later"] = {"x": 1}
        checks = fixture("client-checks.json")
        checks["entries"][0]["later"] = True
        code, text, _ = Run(server=server, checks=checks)()
        self.assertEqual(code, 0)
        self.assertNotIn("later", text)


if __name__ == "__main__":
    unittest.main()
