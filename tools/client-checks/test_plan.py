# SPDX-License-Identifier: MIT
"""Tests for plan.py: reading [ClientCheck] attributes from C# sources, sessions, gates and the problems it names.

    python3 -m unittest discover -s tools/client-checks
"""
from __future__ import annotations

import json
import importlib.util
import os
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import plan  # noqa: E402

REPO = HERE.parent.parent

SOURCE = r'''
public sealed partial class ClientPassMod
{
    private const string Dev28 = "0.2.0-dev.28";
    private const string Dev27 = "0.2.0-dev.27";
    private const string VanillaClient = "vanilla-client";
    private const string Prefix = "03-";

    [ClientCheck("03-c11", "Final snapshot, with (parentheses) and a \"quote\"", Area = "Camera", Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 2201)]
    public Task A(CheckContext c) => Outside(c, "text with [ClientCheck( inside a string is not an attribute");

    [ClientCheck(Prefix + "c7", "Stock-mode lock on vanilla", Area = "Camera", Since = Dev28,
        Tags = [VanillaClient, ClientCheckTags.Visual], Needs = ClientCheckNeeds.Joined, Order = 2202)]
    public Task B(CheckContext c) => null;

    [ClientCheck("03-c10", "Guest transport", Area = "Camera", Since = Dev28, Tags = [TwoPlayers], Order = 2203)]
    public Task C(CheckContext c) => null;

    [ClientCheck("11-join", "Joined", Area = "Replication", Since = Dev28, Needs = ClientCheckNeeds.Joined, Order = 3301)]
    public Task D(CheckContext c) => null;

    [ClientCheck("12-debug", "Debugger", Area = "Tooling", Since = Dev28, Tags = ["host-tools"], Order = 3401)]
    public Task E(CheckContext c) => null;

    [ClientCheck("13-set", "SET", Area = "Frameworks", Since = Dev28, Tags = [Frameworks, TwoPlayers], Order = 3501)]
    public Task F(CheckContext c) => null;

    [ClientCheck("old.check", "An older check", Area = "Core", Since = Dev27, Order = 2201)]
    public Task G(CheckContext c) => null;
}
'''


class Fixture:
    def __init__(self, source: str = SOURCE, index: list[dict] | None = None, gates: dict | None = None):
        self.dir = tempfile.TemporaryDirectory()
        root = Path(self.dir.name)
        pack = root / "mods" / "tests" / "client-pass"
        pack.mkdir(parents=True)
        (pack / "Dev28Camera.cs").write_text(source, encoding="utf-8")
        ids = ["03-c11", "03-c7", "03-c10", "11-join", "12-debug", "13-set"]
        (pack / "dev28-checks.json").write_text(json.dumps(
            {"format": 1, "checks": index if index is not None else [{"id": i, "state": "open"} for i in ids]}))
        gdir = root / "native" / "core" / "gates"
        gdir.mkdir(parents=True)
        for name, doc in (gates or {}).items():
            (gdir / name).write_text(json.dumps(doc))
        self.root = str(root)

    def __del__(self):
        self.dir.cleanup()

    def plan(self) -> dict:
        return plan.build(plan.read_pack(os.path.join(self.root, "mods", "tests", "client-pass")),
                          plan.read_index(os.path.join(self.root, "mods", "tests", "client-pass", "dev28-checks.json")),
                          plan.read_gates(os.path.join(self.root, "native", "core", "gates")), "0.2.0-dev.28")


class ReadPack(unittest.TestCase):
    def test_attributes_are_read_with_constants_strings_and_tags(self):
        f = Fixture()
        checks = {c["id"]: c for c in plan.read_pack(os.path.join(f.root, "mods", "tests", "client-pass"))}
        self.assertEqual(set(checks), {"03-c11", "03-c7", "03-c10", "11-join", "12-debug", "13-set", "old.check"})
        a = checks["03-c11"]
        self.assertEqual(a["title"], 'Final snapshot, with (parentheses) and a "quote"')
        self.assertEqual((a["area"], a["since"], a["tags"], a["needs"], a["order"]),
                         ("Camera", "0.2.0-dev.28", ["visual"], "hosted", 2201))
        self.assertEqual(checks["03-c7"]["tags"], ["vanilla-client", "visual"])  # a pack constant and a qualified name
        self.assertEqual(checks["12-debug"]["tags"], ["host-tools"])             # a literal
        self.assertEqual(checks["03-c10"]["needs"], "any")
        self.assertEqual(checks["old.check"]["since"], "0.2.0-dev.27")

    def test_an_id_that_is_not_a_constant_stops_the_read(self):
        f = Fixture(SOURCE.replace('"03-c10"', "Compute()"))
        with self.assertRaises(plan.PlanError):
            plan.read_pack(os.path.join(f.root, "mods", "tests", "client-pass"))

    def test_the_real_pack_reads(self):
        checks = plan.read_pack(str(REPO / "mods" / "tests" / "client-pass"))
        self.assertGreater(len(checks), 100)
        self.assertEqual(len({c["id"] for c in checks}), len(checks), "ids are unique in the pack")


class Sessions(unittest.TestCase):
    def test_each_check_is_played_in_the_first_session_that_fits(self):
        p = Fixture().plan()
        got = {s["key"]: [c["id"] for c in s["checks"]] for s in p["sessions"]}
        self.assertEqual(got, {"A": ["03-c11"], "B": ["11-join"], "C": ["03-c7"], "D": ["03-c10"], "E": ["12-debug"],
                               "F": ["13-set"]})
        self.assertEqual(p["checks"], 6)          # the older release's check is not planned
        self.assertEqual(p["problems"], [])       # ... and its equal order is no clash
        self.assertEqual([s["minutes"] for s in p["sessions"]], [plan.MINUTES] * 6)

    def test_render_names_every_session_and_says_no_problems(self):
        text = plan.render(Fixture().plan())
        for key in "ABCDEF":
            self.assertIn(f"Session {key}:", text)
        self.assertIn("No problems.", text)


class Problems(unittest.TestCase):
    def test_an_open_id_without_a_check_is_a_problem_unless_deferred(self):
        index = [{"id": i, "state": "open"} for i in ("03-c11", "03-c7", "03-c10", "11-join", "12-debug", "13-set")]
        index += [{"id": "04-m09", "state": "open"}, {"id": "04-m10", "state": "open", "deferred": "needs a second account"},
                  {"id": "05a-c1", "state": "settled"}]
        p = Fixture(index=index).plan()
        self.assertEqual(p["missing"], ["04-m09"])
        self.assertEqual(p["deferred"], [{"id": "04-m10", "reason": "needs a second account"}])
        self.assertEqual(p["settled_without_check"], ["05a-c1"])
        self.assertEqual(len(p["problems"]), 1)
        self.assertIn("04-m09", p["problems"][0])

    def test_a_check_the_index_does_not_have_is_a_problem(self):
        p = Fixture(index=[{"id": "03-c11", "state": "open"}]).plan()
        self.assertEqual(len([x for x in p["problems"] if "not in the check index" in x]), 5)

    def test_duplicate_ids_orders_and_upper_case(self):
        src = SOURCE.replace('"11-join"', '"03-c11"').replace("Order = 3401", "Order = 3501").replace('"13-set"', '"13-SET"')
        p = Fixture(src).plan()
        text = "\n".join(p["problems"])
        self.assertIn("two checks have the id 03-c11", text)
        self.assertIn("share the order 3501", text)
        self.assertIn("13-SET", text)
        self.assertIn("ids are lower-case", text)

    def test_gates_name_checks_of_the_pack(self):
        gates = {"dev28-03-camera.json": {"gates": [
            {"name": "camera.fov", "checks": ["03-c11", "03-c7"]},
            {"name": "camera.shake", "checks": ["03-c5"], "side": "client"},
            {"name": "camera.empty", "checks": []}]}}
        p = Fixture(gates=gates).plan()
        rows = {g["name"]: g for g in p["gates"]}
        self.assertEqual(rows["camera.fov"]["sessions"], ["A", "C"])
        self.assertEqual(rows["camera.shake"]["unknown"], ["03-c5"])
        self.assertEqual(rows["camera.shake"]["side"], "client")
        text = "\n".join(p["problems"])
        self.assertIn("gate camera.shake (dev28-03-camera.json) names the check 03-c5", text)
        self.assertIn("gate camera.empty (dev28-03-camera.json) names no check", text)
        self.assertNotIn("camera.fov", text)


class Main(unittest.TestCase):
    def test_strict_exits_1_only_with_problems_and_2_without_sources(self):
        import contextlib
        import io
        good = Fixture()
        bad = Fixture(index=[{"id": "03-c11", "state": "open"}, {"id": "09-x", "state": "open"}])
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(plan.main(["--repo", good.root, "--strict"]), 0)
            self.assertEqual(plan.main(["--repo", bad.root]), 0)
            self.assertEqual(plan.main(["--repo", bad.root, "--strict"]), 1)
            self.assertEqual(plan.main(["--repo", os.path.join(good.root, "nowhere")]), 2)

    def test_the_repository_plan_can_be_built(self):
        import contextlib
        import io
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(plan.main(["--repo", str(REPO), "--json"]), 0)
        doc = json.loads(out.getvalue())
        self.assertEqual(doc["index"], len(plan.read_index(str(REPO / "mods" / "tests" / "client-pass" / "dev28-checks.json"))))


class GateGenerator(unittest.TestCase):
    def test_trailing_newlines_cannot_reach_cpp_string_literals(self):
        spec = importlib.util.spec_from_file_location("gate_gen", REPO / "native/core/gates/gen.py")
        gen = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(gen)
        for key, value in (("name", "camera.fov\n"), ("checks", ["03-c11\n"])):
            with self.subTest(key=key), tempfile.TemporaryDirectory() as folder:
                row = {"name": "camera.fov", "checks": ["03-c11"], key: value}
                Path(folder, "gate.json").write_text(json.dumps({"gates": [row]}))
                with self.assertRaises(SystemExit):
                    gen.load(folder)


if __name__ == "__main__":
    unittest.main()
