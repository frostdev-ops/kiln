#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""annotate.py: the lines it adds to the XML docs (thread, side and capability; since; extension members; samples).

  python3 -I tools/docs-site/test_annotate.py
"""
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap
import unittest
from unittest import mock
from xml.sax.saxutils import quoteattr
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


annotate = load("annotate", "annotate.py")
samples = load("samples", "samples.py")
HEADER = "kind\tproject\tentry\trelease\tmod_api\n"


def item(uid, cid, kind, assembly="Kiln.Blocks", namespace="Kiln", name=None, parent=None, attributes=(), summary=False):
    """One item of a ManagedReference file, as docfx metadata writes it (summary: DocFX found one for it)."""
    lines = [f"- uid: {uid}", f"  commentId: {cid}", f"  id: {uid.rsplit('.', 1)[-1]}"]
    if parent:
        lines.append(f"  parent: {parent}")
    lines += [f"  name: {name or uid.rsplit('.', 1)[-1]}", f"  type: {kind}", "  assemblies:", f"  - {assembly}", f"  namespace: {namespace}"]
    if summary:
        lines.append("  summary: Found by DocFX.")
    if attributes:
        lines.append("  attributes:")
        for a, args in attributes:
            lines += [f"  - type: Kiln.{a}Attribute", f"    ctor: Kiln.{a}Attribute.#ctor", "    arguments:" if args else "    arguments: []"]
            for v in args:
                lines += ["    - type: System.String", f"      value: {v}"]
    return "\n".join(lines) + "\n"


def xml(assembly, members):
    body = "".join(f"<member name={quoteattr(n)}>{inner}</member>" for n, inner in members.items())
    return f"<?xml version='1.0'?><doc><assembly><name>{assembly}</name></assembly><members>{body}</members></doc>"


class Fixture(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = Path(self.tmp.name)
        self.repo, self.api, self.stage = root / "repo", root / "api", root / "stage"
        for d in (self.api, self.stage):
            d.mkdir()
        mock.patch.dict(os.environ, {"KILN_DOCS_TRACKED": "", "KILN_DOCS_RELEASE": "", "KILN_SDK_VERSION": ""}).start()
        self.addCleanup(mock.patch.stopall)
        self.write_repo()

    def write_repo(self):
        r = self.repo
        (r / "managed/Kiln.Api").mkdir(parents=True)
        (r / "managed/Kiln.Api/Mods.cs").write_text('public const string ApiVersion = "0.2.15";\n')
        rows = [("Kiln.Blocks", "Kiln.Is", "0.2.0-dev.26", "0.2.1", True), ("Kiln.Blocks", "Kiln.Is.Chance(double chance) -> Kiln.RuleCondition!", "0.2.0-dev.28", "0.2.3", False),
                ("Kiln.Blocks", "static Kiln.AiRulesApi.AlliesNear(int count, double metres) -> Kiln.RuleCondition!", "0.2.0-dev.30", "0.2.12", False),
                ("Kiln.Blocks", "static Kiln.AiRulesApi.get_InCombat() -> Kiln.RuleCondition!", "0.2.0-dev.30", "0.2.12", False),
                ("Kiln.Chat", "Kiln.Chat", "<=0.2.0-dev.26", "<=0.2.1", True), ("Kiln.Chat", "static Kiln.Chat.Send(string! text) -> void", "0.2.0-dev.28", "0.2.3", True),
                ("Kiln.Chat", "Kiln.Quiet", "0.2.0-dev.30", "0.2.12", True), ("Kiln.Chat", "Kiln.Quiet.Ok.get -> bool", "0.2.0-dev.30", "0.2.12", True),
                ("Kiln.Chat", "override Kiln.Chat.ToString() -> string!", "0.2.0-dev.30", "0.2.12", True),
                ("Kiln.Chat", "static Kiln.Chat.Send(string! text) -> void", "0.2.0-dev.28", "0.2.3", True)]
        (r / "managed/api-history.tsv").write_text(HEADER + "".join(f"managed\t{p}\t{e}\t{rel}\t{m}\n" for p, e, rel, m, _ in rows))
        blocks = r / "managed/services/Kiln.Blocks"
        blocks.mkdir(parents=True)
        (blocks / "PublicAPI.Shipped.txt").write_text("#nullable enable\n" + "\n".join([
            "Kiln.AiRulesApi.extension(Kiln.Is!)",
            "static Kiln.AiRulesApi.extension(Kiln.Is!).AlliesNear(int count, double metres) -> Kiln.RuleCondition!",
            "static Kiln.AiRulesApi.extension(Kiln.Is!).InCombat.get -> Kiln.RuleCondition!",
            "static Kiln.AiRulesApi.AlliesNear(int count, double metres) -> Kiln.RuleCondition!",
            "static Kiln.AiRulesApi.get_InCombat() -> Kiln.RuleCondition!"]) + "\n")
        (blocks / "PublicAPI.Unshipped.txt").write_text("#nullable enable\nstatic Kiln.Chat.Whisper(string! to, string! text) -> void\n")
        s = r / "mods/samples"
        (s / "Talk").mkdir(parents=True)
        (s / "Talk/Talk.csproj").write_text("<Project><PropertyGroup><KilnServices>Chat</KilnServices></PropertyGroup></Project>")
        (s / "Talk/TalkMod.cs").write_text(textwrap.dedent('''
            using Kiln;
            // Quiet is only a comment word here
            class TalkMod { void Run() { Chat.Send("Quiet"); var msg = "Is"; } }
        '''))
        (s / "Own/OwnMod.cs").parent.mkdir()
        (s / "Own/OwnMod.cs").write_text("class Chat { }\nclass Other { int x; void F(Foo f) { f.Quiet = 1; } }\n")
        subprocess.run(["git", "init", "-q", str(r)], check=True)
        subprocess.run(["git", "-C", str(r), "add", "-A"], check=True)
        # an untracked experiment never counts
        (s / "Untracked").mkdir()
        (s / "Untracked/Experiment.cs").write_text("class E { void F() { Chat.Send(\"x\"); Is.Chance(1); } }\n")

    def write_api(self, *items):
        (self.api / "Kiln.yml").write_text("### YamlMime:ManagedReference\nitems:\n" + "".join(items) + "references: []\n")

    def write_stage(self, assembly, members):
        (self.stage / f"{assembly}.xml").write_text(xml(assembly, members))

    def run_annotate(self, repo=True):
        return annotate.annotate(self.api, self.stage, self.repo if repo else None)

    def member(self, assembly, cid):
        tree = ET.parse(self.stage / f"{assembly}.xml")
        return next((m for m in tree.iter("member") if m.get("name") == cid), None)

    @staticmethod
    def text(element):
        return "".join(element.itertext()) if element is not None else None


class Lines(Fixture):
    def test_each_member_gets_a_since_line_after_its_attribute_line(self):
        self.write_api(item("Kiln.Chat.Send(System.String)", "M:Kiln.Chat.Send(System.String)", "Method", "Kiln.Chat",
                            attributes=[("TickThreadOnly", []), ("RequiresCapability", ["chat.send"])]))
        self.write_stage("Kiln.Chat", {"M:Kiln.Chat.Send(System.String)": "<summary>Says it.</summary>"})
        self.run_annotate()
        summary = self.member("Kiln.Chat", "M:Kiln.Chat.Send(System.String)").find("summary")
        paras = [self.text(p) for p in summary.findall("para")]
        self.assertEqual(paras, ["Thread: tick thread only · Capability: chat.send", "Since mod API 0.2.3 (0.2.0-dev.28)"])
        self.assertTrue(summary.text.startswith("Says it."))

    def test_an_unshipped_member_is_since_the_next_release(self):
        self.write_api(item("Kiln.Chat.Whisper(System.String,System.String)", "M:Kiln.Chat.Whisper(System.String,System.String)", "Method", "Kiln.Chat"))
        self.write_stage("Kiln.Chat", {"M:Kiln.Chat.Whisper(System.String,System.String)": "<summary>Whispers.</summary>"})
        self.run_annotate()
        self.assertIn("Since mod API 0.2.15 (next release)",
                      self.text(self.member("Kiln.Chat", "M:Kiln.Chat.Whisper(System.String,System.String)").find("summary")))

    def test_an_undocumented_member_gets_the_line_in_remarks_and_stays_undocumented(self):
        self.write_api(item("Kiln.Quiet", "T:Kiln.Quiet", "Class", "Kiln.Chat"))
        self.write_stage("Kiln.Chat", {})
        self.run_annotate()
        member = self.member("Kiln.Chat", "T:Kiln.Quiet")
        self.assertIsNone(member.find("summary"))
        self.assertEqual(self.text(member.find("remarks")), "Since mod API 0.2.12 (0.2.0-dev.30)")

    def test_a_summary_docfx_finds_elsewhere_is_kept(self):
        # a positional record's property is documented by the record's <param>; an override, by the member it overrides
        self.write_api(
            item("Kiln.Quiet", "T:Kiln.Quiet", "Class", "Kiln.Chat", summary=True),
            item("Kiln.Quiet.Ok", "P:Kiln.Quiet.Ok", "Property", "Kiln.Chat", parent="Kiln.Quiet", summary=True),
            item("Kiln.Chat.ToString", "M:Kiln.Chat.ToString", "Method", "Kiln.Chat", parent="Kiln.Chat", summary=True),
            item("Kiln.Chat.Send(System.String)", "M:Kiln.Chat.Send(System.String)", "Method", "Kiln.Chat", parent="Kiln.Chat"))
        self.write_stage("Kiln.Chat", {"T:Kiln.Quiet": '<summary>A result.</summary><param name="Ok">Whether it <c>worked</c>.</param>'})
        self.run_annotate()
        ok = self.member("Kiln.Chat", "P:Kiln.Quiet.Ok")
        self.assertEqual(self.text(ok.find("summary")), "Whether it worked. Since mod API 0.2.12 (0.2.0-dev.30)")
        self.assertIsNotNone(ok.find("summary").find("c"))
        self.assertIsNone(ok.find("inheritdoc"))
        override = self.member("Kiln.Chat", "M:Kiln.Chat.ToString")
        self.assertIsNotNone(override.find("inheritdoc"))
        # nothing found by DocFX, nothing promised: the member stays without a summary
        send = self.member("Kiln.Chat", "M:Kiln.Chat.Send(System.String)")
        self.assertIsNone(send.find("inheritdoc"))
        self.assertIsNone(send.find("summary"))
        self.assertIn("Since mod API 0.2.3", self.text(send.find("remarks")))

    def test_a_member_the_ledger_does_not_know_gets_no_line(self):
        self.write_api(item("Kiln.Chat.Hidden", "M:Kiln.Chat.Hidden", "Method", "Kiln.Chat"))
        self.write_stage("Kiln.Chat", {"M:Kiln.Chat.Hidden": "<summary>No.</summary>"})
        self.assertEqual(self.run_annotate()["since"], 0)
        self.assertEqual(self.text(self.member("Kiln.Chat", "M:Kiln.Chat.Hidden").find("summary")), "No.")

    def test_without_a_repository_only_the_attribute_lines_are_added(self):
        self.write_api(item("Kiln.Chat.Send(System.String)", "M:Kiln.Chat.Send(System.String)", "Method", "Kiln.Chat",
                            attributes=[("ServerOnly", [])]))
        self.write_stage("Kiln.Chat", {"M:Kiln.Chat.Send(System.String)": "<summary>Says it.</summary>"})
        self.run_annotate(repo=False)
        self.assertEqual(self.text(self.member("Kiln.Chat", "M:Kiln.Chat.Send(System.String)").find("summary")),
                         "Says it. Side: server only")


class Extensions(Fixture):
    def setUp(self):
        super().setUp()
        self.write_api(
            item("Kiln.Is", "T:Kiln.Is", "Class", "Kiln.Blocks"),
            item("Kiln.AiRulesApi", "T:Kiln.AiRulesApi", "Class", "Kiln.Blocks"),
            item("Kiln.AiRulesApi.get_InCombat", "M:Kiln.AiRulesApi.get_InCombat", "Method", "Kiln.Blocks", parent="Kiln.AiRulesApi"),
            item("Kiln.AiRulesApi.AlliesNear(System.Int32,System.Double)", "M:Kiln.AiRulesApi.AlliesNear(System.Int32,System.Double)",
                 "Method", "Kiln.Blocks", parent="Kiln.AiRulesApi"))
        marker = "&lt;G&gt;$3AF4"
        self.write_stage("Kiln.Blocks", {
            "T:Kiln.Is": "<summary>Conditions.</summary><remarks>Existing remark.</remarks>",
            "M:Kiln.AiRulesApi.get_InCombat": f'<inheritdoc cref="P:Kiln.AiRulesApi.{marker}.InCombat" />',
            f"P:Kiln.AiRulesApi.<G>$3AF4.InCombat": "<summary>It is fighting.</summary>",
            "M:Kiln.AiRulesApi.AlliesNear(System.Int32,System.Double)": "<summary>Allies are near. More text.</summary>"})
        self.counts = self.run_annotate()

    def test_a_compiled_member_says_how_it_reads_in_code(self):
        summary = self.member("Kiln.Blocks", "M:Kiln.AiRulesApi.AlliesNear(System.Int32,System.Double)").find("summary")
        para = [p for p in summary.findall("para") if self.text(p).startswith("Extension member")][0]
        self.assertEqual(self.text(para), "Extension member of Is: in code it reads Is.AlliesNear(int count, double metres).")
        self.assertEqual(para.find("see").get("cref"), "T:Kiln.Is")
        self.assertEqual(para.find("c").text, "Is.AlliesNear(int count, double metres)")
        # a property: no parentheses; its doc is an <inheritdoc>, so the lines go to <remarks>
        remarks = self.member("Kiln.Blocks", "M:Kiln.AiRulesApi.get_InCombat").find("remarks")
        self.assertIn("in code it reads Is.InCombat.", self.text(remarks))
        self.assertEqual(self.counts["extension"], 2)

    def test_the_receiver_lists_its_extension_members_with_links(self):
        remarks = self.member("Kiln.Blocks", "T:Kiln.Is").find("remarks")
        self.assertTrue(remarks.text.startswith("Existing remark."))
        text = self.text(remarks)
        self.assertIn("Extension members.", text)
        self.assertIn("From AiRulesApi (Kiln.Blocks):", text)
        links = {s.get("cref"): self.text(s) for s in remarks.iter("see")}
        self.assertEqual(links["M:Kiln.AiRulesApi.get_InCombat"], "Is.InCombat")
        self.assertEqual(links["M:Kiln.AiRulesApi.AlliesNear(System.Int32,System.Double)"], "Is.AlliesNear(int count, double metres)")
        rows = [self.text(i) for i in remarks.iter("item")]
        self.assertEqual(rows, ["Is.AlliesNear(int count, double metres): Allies are near.", "Is.InCombat: It is fighting."])
        self.assertEqual(self.counts["receivers"], 1)


class Samples(Fixture):
    def test_a_type_links_the_tracked_samples_that_use_it(self):
        self.write_api(item("Kiln.Chat", "T:Kiln.Chat", "Class", "Kiln.Chat"), item("Kiln.Quiet", "T:Kiln.Quiet", "Class", "Kiln.Chat"),
                       item("Kiln.Is", "T:Kiln.Is", "Class", "Kiln.Blocks"))
        self.write_stage("Kiln.Chat", {"T:Kiln.Chat": "<summary>Chat.</summary>"})
        self.write_stage("Kiln.Blocks", {"T:Kiln.Is": "<summary>Is.</summary>"})
        self.run_annotate()
        remarks = self.member("Kiln.Chat", "T:Kiln.Chat").find("remarks")
        links = [(s.get("href"), s.text) for s in remarks.iter("see")]
        # Talk uses Chat; Own declares its own class Chat; the untracked experiment does not count
        self.assertEqual(links, [("https://github.com/frostdev-ops/kiln/blob/main/mods/samples/Talk/TalkMod.cs", "Talk/TalkMod.cs")])
        self.assertTrue(self.text(remarks).startswith("Used in samples: "))
        # a word in a comment or a string, or a member of something else (`f.Quiet`), is no use; neither is the untracked
        # experiment's `Is.Chance`
        for assembly, cid in (("Kiln.Chat", "T:Kiln.Quiet"), ("Kiln.Blocks", "T:Kiln.Is")):
            self.assertNotIn("Used in samples", self.text(self.member(assembly, cid).find("remarks")) or "", cid)

    def test_a_long_list_is_cut(self):
        s = self.repo / "mods/samples"
        for n in range(12):
            (s / f"S{n:02}").mkdir()
            (s / f"S{n:02}/M.cs").write_text("class M { void F() { Chat.Send(\"x\"); } }\n")
        subprocess.run(["git", "-C", str(self.repo), "add", "mods/samples/S*"], check=True)
        self.write_api(item("Kiln.Chat", "T:Kiln.Chat", "Class", "Kiln.Chat"))
        self.write_stage("Kiln.Chat", {"T:Kiln.Chat": "<summary>Chat.</summary>"})
        self.run_annotate()
        text = self.text(self.member("Kiln.Chat", "T:Kiln.Chat").find("remarks"))
        self.assertIn(" and 5 more", text)


class Tiers(Fixture):
    def test_each_type_says_its_tier_from_tiers_json(self):
        (self.repo / "tools/docs-site").mkdir(parents=True)
        (self.repo / "tools/docs-site/tiers.json").write_text(
            '{"format": 1, "assemblies": {"Kiln.Chat": "stable", "Kiln.Blocks": "native"}, "types": {"Kiln.Quiet": "advanced"}}')
        self.write_api(item("Kiln.Chat", "T:Kiln.Chat", "Class", "Kiln.Chat"), item("Kiln.Quiet", "T:Kiln.Quiet", "Class", "Kiln.Chat"),
                       item("Kiln.Is", "T:Kiln.Is", "Class", "Kiln.Blocks"),
                       item("Kiln.Chat.Send(System.String)", "M:Kiln.Chat.Send(System.String)", "Method", "Kiln.Chat", parent="Kiln.Chat"))
        self.write_stage("Kiln.Chat", {"T:Kiln.Chat": "<summary>Chat.</summary>", "T:Kiln.Quiet": "<summary>Quiet.</summary>",
                                       "M:Kiln.Chat.Send(System.String)": "<summary>Says it.</summary>"})
        self.write_stage("Kiln.Blocks", {"T:Kiln.Is": "<summary>Is.</summary>"})
        counts = self.run_annotate()
        self.assertEqual(counts["tier"], 3)
        self.assertIn("Tier: Stable", self.text(self.member("Kiln.Chat", "T:Kiln.Chat").find("summary")))
        self.assertIn("Tier: Advanced", self.text(self.member("Kiln.Chat", "T:Kiln.Quiet").find("summary")))
        self.assertIn("Tier: Native", self.text(self.member("Kiln.Blocks", "T:Kiln.Is").find("summary")))
        self.assertNotIn("Tier", self.text(self.member("Kiln.Chat", "M:Kiln.Chat.Send(System.String)").find("summary")))  # types only

    def test_every_assembly_of_the_reference_has_a_tier(self):
        repo = HERE.parents[1]
        tiers = annotate.load_tiers(repo)
        import json as _json
        services = _json.loads((HERE / "services.json").read_text(encoding="utf-8"))
        missing = [s["assembly"] for s in services["services"] if s["assembly"] not in tiers["assemblies"]]
        self.assertEqual(missing, [], "add these assemblies to tools/docs-site/tiers.json (docs/kiln/modding/tiers.md)")
        self.assertTrue(set(tiers["assemblies"].values()) | set(tiers["types"].values()) <= set(annotate.TIER_TEXT))


class SampleScan(unittest.TestCase):
    def test_what_counts_as_a_use(self):
        used = samples.chains('''
            using Kiln;
            using static Kiln.Items;
            // Ignored in a comment
            /* Ignored too */
            class Declared { Foo f; Kiln.Bar b; x.Position = 1; void G() { Abilities.Attributes.Get(); } }
            var s = "InAString"; var t = $"{Interp}"; var c = 'C'; var r = """Raw""";
        ''')
        for yes in ("Items", "Foo", "Bar", "Abilities.Attributes", "Abilities"):
            self.assertIn(yes, used)
        for no in ("Ignored", "InAString", "Position", "Declared", "Interp", "Raw"):
            self.assertNotIn(no, used)

    def test_an_attribute_is_named_without_its_suffix(self):
        s = samples.Samples.__new__(samples.Samples)
        s.files = {"mods/samples/A/A.cs": samples.chains("[TickThreadOnly] class A { }")}
        self.assertEqual(s.use("TickThreadOnlyAttribute"), ["mods/samples/A/A.cs"])


class Cli(Fixture):
    def test_the_command_line_runs_with_and_without_a_repository(self):
        self.write_api(item("Kiln.Chat", "T:Kiln.Chat", "Class", "Kiln.Chat"))
        self.write_stage("Kiln.Chat", {"T:Kiln.Chat": "<summary>Chat.</summary>"})
        env = {**os.environ, "KILN_DOCS_TRACKED": ""}
        r = subprocess.run([sys.executable, "-I", str(HERE / "annotate.py"), "--repo", str(self.repo), str(self.api), str(self.stage)],
                           capture_output=True, text=True, env=env)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("1 with a since line", r.stdout)
        r = subprocess.run([sys.executable, "-I", str(HERE / "annotate.py"), str(self.api)], capture_output=True, text=True, env=env)
        self.assertNotEqual(r.returncode, 0)


if __name__ == "__main__":
    unittest.main()
