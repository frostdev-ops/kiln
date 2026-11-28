#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests for tools/docs-site/drift.py, the doc drift checks: names (members, nested types, enum values, inherited members,
C# 14 and classic extension members, property chains, what is skipped), versions (claims before or after a name, table cells,
inexact ledger rows, unshipped names, mod API claims), facts (pins of the SDK and the mod API, the versions.md rows), map
(docs-map pages, wiki pages, samples, links and anchors), the allow-list (suppress, stale entry, missing reason) and the
exit codes.

  python3 -I tools/docs-site/test_drift.py
Builds small repositories under a temporary folder; nothing else is written. With KILN_DRIFT_REAL=1 it also runs the checks
over this repository (the same run as tools/check.sh's stage `drift`).
"""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("drift", HERE / "drift.py")
drift = importlib.util.module_from_spec(spec)
spec.loader.exec_module(drift)

API = """#nullable enable
Kiln.Mods
const Kiln.Mods.ApiVersion = "0.2.15" -> string!
Kiln.EntityId
Kiln.Items
static Kiln.Items.Give(Kiln.EntityId target, int count = 1) -> int
static Kiln.Items.Count.get -> int
Kiln.Ui
static Kiln.Ui.Window(string! title) -> void
static Kiln.Ui.Tooltip.get -> Kiln.UiTooltip!
Kiln.Ui.Inventory
static Kiln.Ui.Inventory.OnClick(System.Action! a) -> void
Kiln.UiTooltip
Kiln.UiTooltip.AddLines(string! line) -> void
Kiln.GameEvent
Kiln.GameEvent.Cancel() -> void
Kiln.GameEvent.Tick.get -> int
Kiln.HitEvent
Kiln.HitEvent.Amount.get -> int
Kiln.HitEvent.Amount.set -> void
Kiln.ILabelled
Kiln.ILabelled.Label.get -> string!
Kiln.Crate
Kiln.Crate.Crate() -> void
Kiln.Mode
Kiln.Mode.Fast = 1 -> Kiln.Mode
Kiln.Mode.Slow = 2 -> Kiln.Mode
Kiln.Is
Kiln.Act
Kiln.AiRulesApi
Kiln.AiRulesApi.extension(Kiln.Is!)
Kiln.AiRulesApi.extension(Kiln.Act!)
static Kiln.AiRulesApi.extension(Kiln.Act!).Flee() -> void
static Kiln.AiRulesApi.extension(Kiln.Is!).InCombat.get -> bool
Kiln.StringTools
static Kiln.StringTools.Shout(this Kiln.Crate! c) -> string!
Kiln.Ecs
Kiln.Chat
static Kiln.Chat.Say(string! text) -> void
Kiln.Newer
Kiln.Newer.Thing() -> void
Kiln.Older
static Kiln.Older.Go() -> void
Kiln.Fresh
static Kiln.Fresh.Go() -> void
"""
UNSHIPPED = """#nullable enable
static Kiln.Fresh.Go2() -> void
"""
SOURCE = """
namespace Kiln;
public class GameEvent { }
public sealed class HitEvent : GameEvent, ILabelled { }
public interface ILabelled { }
public sealed class Crate : System.Exception { }
public sealed record Mode2(int A) : GameEvent;
"""
LEDGER_ROWS = [
    ("Kiln.Mods", "0.2.0-dev.27", "0.2.2"),
    ("Kiln.Items", "0.2.0-dev.27", "0.2.2"),
    ("static Kiln.Items.Give(Kiln.EntityId target, int count = 1) -> int", "0.2.0-dev.27", "0.2.2"),
    ("static Kiln.Items.Count.get -> int", "0.2.0-dev.29.15", "0.2.7"),
    ("Kiln.Ui", "0.2.0-dev.27", "0.2.2"),
    ("static Kiln.Ui.Tooltip.get -> Kiln.UiTooltip!", "0.2.0-dev.30", "0.2.12"),
    ("Kiln.UiTooltip.AddLines(string! line) -> void", "0.2.0-dev.30", "0.2.12"),
    ("Kiln.Older", "<=0.2.0-dev.26", "<=0.2.1"),
    ("static Kiln.Older.Go() -> void", "<=0.2.0-dev.26", "<=0.2.1"),
    ("Kiln.Fresh", "0.2.0-dev.30.5", "0.2.14"),
    ("static Kiln.Fresh.Go() -> void", "0.2.0-dev.30.5", "0.2.14"),
    ("Kiln.Chat", "0.2.0-dev.29", "0.2.4"),
    ("static Kiln.Chat.Say(string! text) -> void", "0.2.0-dev.29", "0.2.4"),
]
VERSIONS = """# Builds
| Build | Mod API | Use the MDK | New for mod authors |
|---|---|---|---|
| dev.27 | 0.2.2 | dev.27 | a |
| dev.29 | 0.2.4 | dev.29 | b |
| dev.29.15 | 0.2.7 | dev.29.15 | c |
| dev.30 | 0.2.12 | dev.30 | d |
| dev.30.5 | 0.2.14 | dev.30.5 | e |
| dev.30.6 | 0.2.15 | dev.30.6 | f |
"""
SETUP = """# Setup
The MDK is `0.2.0-dev.30.6` for this release.
<Project Sdk="Kiln.Sdk/0.2.0-dev.30.6">
| `KilnApiVersion` | the SDK's (0.2.15 for dev.30.6) | `apiVersion` |
"""
DOCS_MAP = """# Where
| Question | Page |
|---|---|
| Start | `first-mod`, `dev-kit` |
Core services (pages in the repository's `docs/kiln/`):
| Question | Page |
|---|---|
| Items | `services/items.md`, `modding/missing.md` |
| Debugging | `services/items.md`; wiki `debugging` |
dev.26+ samples: Alpha, Beta.
"""
SKILL_MD = """# Skill
See [setup](references/setup.md#setup) and [versions](references/versions.md).
"""


class Repo:
    def __init__(self, tmp):
        self.root = Path(tmp)
        self.write("managed/Kiln.Api/Mods.cs", 'public static class Mods { public const string ApiVersion = "0.2.15"; }\n')
        self.write("managed/Kiln.Api/PublicAPI.Shipped.txt", API)
        self.write("managed/Kiln.Api/PublicAPI.Unshipped.txt", UNSHIPPED)
        self.write("managed/Kiln.Api/Types.cs", SOURCE)
        self.ledger(LEDGER_ROWS)
        self.write("agent/skills/kiln-modding/SKILL.md", SKILL_MD)
        self.write("agent/skills/kiln-modding/references/versions.md", VERSIONS)
        self.write("agent/skills/kiln-modding/references/setup.md", SETUP)
        self.write("agent/skills/kiln-modding/references/docs-map.md", DOCS_MAP)
        self.write("docs/kiln/services/items.md", "# Items\n")
        (self.root / "mods/samples/Alpha").mkdir(parents=True)
        (self.root / "mods/samples/Beta").mkdir(parents=True)
        self.wiki = self.root / "wiki"
        self.wiki.mkdir()
        for n in ("first-mod", "dev-kit", "debugging"):
            (self.wiki / f"{n}.md").write_text("# w\n")

    def write(self, rel, text):
        p = self.root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding="utf-8")

    def ledger(self, rows):
        self.write("managed/api-history.tsv", "kind\tproject\tentry\trelease\tmod_api\n" +
                   "".join(f"managed\tKiln.Api\t{e}\t{r}\t{a}\n" for e, r, a in rows))

    def doc(self, text, name="docs/kiln/page.md"):
        self.write(name, text)

    def run(self, *checks, warn=(), allow=None):
        out = io.StringIO()
        code, found = drift.run(self.root, list(checks or drift.CHECKS), set(warn), allow, self.wiki, out)
        return code, found, out.getvalue()


class Case(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.repo = Repo(tmp.name)

    def names(self, text, **kw):
        self.repo.doc(text)
        _, found, _ = self.repo.run("names", **kw)
        return sorted(f.name for f in found["names"])


class Names(Case):
    def test_real_names_pass(self):
        self.assertEqual(self.names("`Items.Give(x)` `Items.Count` `Ui.Window` `Ui.Tooltip.AddLines` `Ui.Inventory.OnClick` "
                                    "`Mode.Fast` `Mode.Slow` `Mods.ApiVersion` `Chat.Say`"), [])

    def test_missing_member_is_found(self):
        self.assertEqual(self.names("`Items.Take` and `Items.Give`"), ["Items.Take"])

    def test_nowhere_and_not_on_this_type(self):
        self.repo.doc("`Items.Take` `Items.Tick`")
        _, found, text = self.repo.run("names")
        self.assertIn("`Items.Take` is no public member of any type", text)
        self.assertIn("`Items.Tick` is no public member of Items", text)       # Tick exists on GameEvent

    def test_a_type_that_is_not_kiln_api_is_ignored(self):
        self.assertEqual(self.names("`Foo.Bar` `Path.Combine` `Kiln.Items` `Ecs.Whatever`"), [])

    def test_a_nested_type_is_a_member(self):
        self.assertEqual(self.names("`Ui.Inventory` `Ui.Missing`"), ["Ui.Missing"])

    def test_inherited_members(self):
        # HitEvent : GameEvent, ILabelled
        self.assertEqual(self.names("`HitEvent.Cancel` `HitEvent.Tick` `HitEvent.Amount` `HitEvent.Label` `HitEvent.Nope`"),
                         ["HitEvent.Nope"])

    def test_object_and_exception_members(self):
        # Crate : System.Exception
        self.assertEqual(self.names("`Crate.Message` `Crate.ToString` `Crate.Nope`"), ["Crate.Nope"])

    def test_extension_members(self):
        self.assertEqual(self.names("`Act.Flee` `Is.InCombat` `Act.InCombat` `Is.Flee`"), ["Act.InCombat", "Is.Flee"])

    def test_classic_extension_method(self):
        self.assertEqual(self.names("`Crate.Shout` `Crate.Whisper`"), ["Crate.Whisper"])

    def test_a_property_chain_is_followed(self):
        # Ui.Tooltip is a property of type UiTooltip
        self.assertEqual(self.names("`Ui.Tooltip.AddLines` `Ui.Tooltip.Gone`"), ["Ui.Tooltip.Gone"])

    def test_a_chain_through_an_unknown_type_is_not_followed(self):
        self.assertEqual(self.names("`Items.Give.Whatever` `Items.Count.Whatever`"), [])

    def test_code_fences(self):
        text = "```csharp\nItems.Take(x);\n```\n```json\n{ \"Items.Take\": 1 }\n```\n```\nUi.Gone();\n```\n"
        self.assertEqual(self.names(text), ["Items.Take", "Ui.Gone"])

    def test_prose_outside_backticks_is_not_read(self):
        self.assertEqual(self.names("Items.Take is a name."), [])

    def test_a_member_of_a_variable_is_not_read(self):
        self.assertEqual(self.names("`ctx.Items.Take`"), [])

    def test_generic_arguments_are_ignored(self):
        self.assertEqual(self.names("`Items.Give<T>(x)` `Items.Take<T>()`"), ["Items.Take"])

    def test_plans_releasenotes_and_schemas_are_not_checked(self):
        for name in ("docs/kiln/x-plan.md", "docs/kiln/release-notes/a.md", "docs/kiln/modding/schemas/s.md",
                     "docs/kiln/status.generated.md", "agent/skills/kiln-modding/references/api-index/Items.md"):
            self.repo.write(name, "`Items.Take`\n")
        _, found, _ = self.repo.run("names")
        self.assertEqual(found["names"], [])
        self.repo.write("agent/skills/kiln-modding/references/api.md", "`Items.Take`\n")
        _, found, _ = self.repo.run("names")
        self.assertEqual([f.path for f in found["names"]], ["agent/skills/kiln-modding/references/api.md"])

    def test_bases_are_read_from_declarations(self):
        got = dict(drift.parse_bases("public sealed record Foo<T>(int A, B C) : Base(A), IThing<T> where T : class;\n"
                                     "internal struct S : IEquatable<S> { }\nclass Plain { }\npublic enum E : byte { A }\n"))
        self.assertEqual(got, {"Foo": ["Base", "IThing"], "S": ["IEquatable"]})


class Stale(Case):
    """dev.32 review-beta1: facts the code contradicts (the skill said there was no pet fake after KilnHarness.Pets landed)."""

    def stale(self, rules, text, name="docs/kiln/page.md"):
        self.repo.write("tools/docs-site/drift-stale.txt", "# comment\n" + rules)
        self.repo.doc(text, name)
        code, found, out = self.repo.run("stale")
        return code, found["stale"], out

    def test_a_listed_phrase_is_found_with_what_is_true(self):
        code, found, out = self.stale("(?i)no pet fake\t*\tKilnHarness.Pets exists\n", "line\nThe kit has No pet fake yet.\n")
        self.assertEqual(code, 1)
        self.assertEqual([(f.path, f.line) for f in found], [("docs/kiln/page.md", 2)])
        self.assertIn("says `No pet fake`: KilnHarness.Pets exists", out)

    def test_the_glob_limits_the_pages(self):
        code, found, _ = self.stale("no pet fake\tagent/*\tKilnHarness.Pets exists\n", "no pet fake\n")
        self.assertEqual((code, found), (0, []))

    def test_release_notes_are_history(self):
        code, found, _ = self.stale("no pet fake\t*\tKilnHarness.Pets exists\n", "no pet fake\n", "docs/kiln/release-notes/0.2.0-dev.31.md")
        self.assertEqual((code, found), (0, []))

    def test_a_line_without_the_truth_or_a_bad_pattern_is_an_error(self):
        code, found, out = self.stale("no pet fake\t*\n([\t*\tx\n", "fine\n")
        self.assertEqual(code, 1)
        self.assertIn("three tab-separated cells expected", out)
        self.assertIn("the pattern does not compile", out)

    def test_no_list_is_no_finding(self):
        self.repo.doc("no pet fake\n")
        code, found, _ = self.repo.run("stale")
        self.assertEqual((code, found["stale"]), (0, []))

    def test_the_repository_says_nothing_listed(self):
        """the real list over the real pages"""
        out = io.StringIO()
        code, found = drift.run(HERE.parent.parent, ["stale"], set(), None, None, out)
        self.assertEqual(found["stale"], [], out.getvalue())


class Versions(Case):
    def versions(self, text):
        self.repo.doc(text)
        _, found, _ = self.repo.run("versions")
        return sorted((f.name, f.line) for f in found["versions"])

    def test_a_claim_before_the_first_release_is_wrong(self):
        self.assertEqual(self.versions("since 0.2.0-dev.29.19: `Ui.Tooltip`"), [("Ui.Tooltip", 1)])
        self.assertEqual(self.versions("dev.29.15+ `Items.Count` `Ui.Tooltip`"), [("Ui.Tooltip", 1)])    # Count is in 29.15

    def test_a_claim_at_or_after_it_is_right(self):
        self.assertEqual(self.versions("dev.30+ `Ui.Tooltip`\nsince 0.2.0-dev.30.2 `Ui.Tooltip`"), [])

    def test_a_mark_after_the_name_counts(self):
        self.assertEqual(self.versions("`Ui.Tooltip` (dev.29.19+)"), [("Ui.Tooltip", 1)])
        # a mark on each side: it is right when either could be meant (`A` (dev.20+), `B` handlers (dev.30+))
        self.assertEqual(self.versions("`Items.Give` (dev.27+), `Ui.Tooltip` lines (dev.30+)"), [])

    def test_a_mark_in_another_table_cell_does_not_reach(self):
        self.assertEqual(self.versions("| a (dev.21+) | `Ui.Tooltip` |"), [])
        self.assertEqual(self.versions("| a (dev.21+) `Ui.Tooltip` | b |"), [("Ui.Tooltip", 1)])

    def test_a_heading_mark_after_the_name(self):
        self.assertEqual(self.versions("### `Ui.Tooltip` (since 0.2.0-dev.29.19)"), [("Ui.Tooltip", 1)])

    def test_mod_api_claims(self):
        self.assertEqual(self.versions("mod API 0.2.4 `Ui.Tooltip`"), [("Ui.Tooltip", 1)])
        self.assertEqual(self.versions("mod API 0.2.12 `Ui.Tooltip`"), [])

    def test_inexact_ledger_rows_prove_nothing(self):
        self.assertEqual(self.versions("dev.20+ `Older.Go`"), [])

    def test_a_new_overload_does_not_make_an_old_name_new(self):
        self.repo.ledger(LEDGER_ROWS + [("static Kiln.Older.Go(int a) -> void", "0.2.0-dev.30", "0.2.12")])
        self.assertEqual(self.versions("dev.27+ `Older.Go`"), [])

    def test_a_name_in_unshipped_is_after_every_release(self):
        # the last release of the ledger is 30.5; Fresh.Go2 is only in PublicAPI.Unshipped.txt
        self.assertEqual(self.versions("since 0.2.0-dev.30.5: `Fresh.Go2`"), [("Fresh.Go2", 1)])
        self.assertEqual(self.versions("since 0.2.0-dev.30.6: `Fresh.Go2`"), [])
        self.assertEqual(self.versions("mod API 0.2.14 `Fresh.Go2`"), [("Fresh.Go2", 1)])
        self.assertEqual(self.versions("mod API 0.2.15 `Fresh.Go2`"), [])

    def test_a_nested_type_claim(self):
        self.assertEqual(self.versions("dev.27+ `Ui.Inventory`"), [])                  # no row of its own: nothing to compare

    def test_a_line_without_a_name_or_a_mark_is_left_alone(self):
        self.assertEqual(self.versions("dev.20+ and `Foo.Bar` since 0.2.0-dev.29"), [])


class Facts(Case):
    def facts(self):
        _, found, _ = self.repo.run("facts")
        return sorted(f.text for f in found["facts"])

    def test_current_is_clean(self):
        self.assertEqual(self.facts(), [])

    def test_an_old_sdk_pin(self):
        self.repo.write("agent/skills/kiln-modding/references/setup.md", SETUP.replace("Kiln.Sdk/0.2.0-dev.30.6", "Kiln.Sdk/0.2.0-dev.29.19"))
        (t,) = self.facts()
        self.assertIn("pins the SDK 0.2.0-dev.29.19, the current one is 0.2.0-dev.30.6", t)

    def test_an_old_build_on_a_this_release_line(self):
        self.repo.write("agent/skills/kiln-modding/references/setup.md", SETUP.replace("`0.2.0-dev.30.6` for this release", "`0.2.0-dev.30.5` for this release"))
        (t,) = self.facts()
        self.assertIn("0.2.0-dev.30.5 as the current build", t)

    def test_an_old_mod_api_in_parentheses(self):
        self.repo.write("agent/skills/kiln-modding/references/api.md", "`Mods.RuntimeApiVersion` is the mod API (0.2.14 on dev.30.5).\n")
        (t,) = self.facts()
        self.assertIn("the current mod API is 0.2.15", t)

    def test_a_since_claim_is_not_a_pin(self):
        self.repo.write("agent/skills/kiln-modding/references/api-items.md", "since 0.2.0-dev.30.3, mod API 0.2.13: `Items.Give`\n")
        self.assertEqual(self.facts(), [])

    def test_other_skill_files_are_not_pins(self):
        self.repo.write("agent/skills/kiln-modding/references/pitfalls.md", "Kiln.Sdk/0.2.0-dev.20\n")
        self.assertEqual(self.facts(), [])

    def test_no_row_for_the_current_mod_api(self):
        self.repo.write("agent/skills/kiln-modding/references/versions.md", VERSIONS.replace("| dev.30.6 | 0.2.15 | dev.30.6 | f |\n", ""))
        (t,) = self.facts()
        self.assertIn("no row for the current mod API 0.2.15", t)

    def test_a_gap_in_the_rows(self):
        self.repo.write("agent/skills/kiln-modding/references/versions.md", VERSIONS.replace("| dev.29.15 | 0.2.7 | dev.29.15 | c |\n", ""))
        (t,) = self.facts()
        self.assertIn("no row for mod API 0.2.7", t)

    def test_a_runtime_only_build_row_does_not_move_the_mdk(self):
        self.repo.write("agent/skills/kiln-modding/references/versions.md",
                        VERSIONS + "| dev.30.7 | 0.2.15 | dev.30.6 | runtime only |\n")
        self.assertEqual(self.facts(), [])


class Map(Case):
    def map(self, wiki=True):
        out = io.StringIO()
        code, found = drift.run(self.repo.root, ["map"], set(), None, self.repo.wiki if wiki else self.repo.root / "none", out)
        return sorted(f.text for f in found["map"]), out.getvalue()

    def test_clean(self):
        self.repo.write("docs/kiln/modding/missing.md", "# m\n")
        self.assertEqual(self.map()[0], [])

    def test_a_missing_repository_page(self):
        found, _ = self.map()
        self.assertEqual(found, ["repository page modding/missing.md is not at docs/kiln/modding/missing.md"])

    def test_a_page_in_another_folder(self):
        self.repo.write("docs/kiln/modding/missing.md", "# m\n")
        self.repo.write("agent/skills/kiln-modding/references/docs-map.md", DOCS_MAP.replace("`modding/missing.md`", "`missing.md`"))
        (t,) = self.map()[0]
        self.assertIn("missing.md is not at docs/kiln/missing.md (it is at docs/kiln/modding/missing.md)", t)

    def test_a_wiki_page(self):
        self.repo.write("docs/kiln/modding/missing.md", "# m\n")
        (self.repo.wiki / "debugging.md").unlink()
        self.assertEqual(self.map()[0], ["wiki page 'debugging' does not exist"])
        found, text = self.map(wiki=False)
        self.assertEqual(found, [])
        self.assertIn("no wiki folder", text)

    def test_a_sample(self):
        self.repo.write("docs/kiln/modding/missing.md", "# m\n")
        self.repo.write("agent/skills/kiln-modding/references/docs-map.md", DOCS_MAP.replace("Alpha, Beta", "Alpha, Gamma"))
        self.assertEqual(self.map()[0], ["sample 'Gamma' is not in mods/samples"])

    def test_links_and_anchors(self):
        self.repo.write("docs/kiln/modding/missing.md", "# m\n")
        self.repo.write("agent/skills/kiln-modding/references/a.md", "[y](setup.md#nope) [z](setup.md#setup) [w](gone.md) "
                        "`docs/kiln/services/items.md` `docs/kiln/gone.md` [v](https://frostdev.io/kiln/wiki/gone)\n")
        got = self.map()[0]
        self.assertEqual(len(got), 4, got)
        for want in ("link target gone.md is missing", "anchor #nope is not in setup.md", "docs/kiln/gone.md does not exist",
                     "wiki page 'gone' does not exist"):
            self.assertIn(want, got)


class Allow(Case):
    def test_an_entry_suppresses_and_a_stale_one_fails(self):
        self.repo.doc("`Items.Take`")
        allow = self.repo.root / "allow.txt"
        allow.write_text("names\tItems.Take\tdocs/kiln/*.md\tdeliberate\n", encoding="utf-8")
        code, found, text = self.repo.run("names", allow=allow)
        self.assertEqual((code, found["names"]), (0, []))
        self.repo.doc("`Items.Give`")
        code, _, text = self.repo.run("names", allow=allow)
        self.assertEqual(code, 1)
        self.assertIn("matches nothing any more", text)

    def test_the_glob_scopes_the_entry(self):
        self.repo.doc("`Items.Take`")
        allow = self.repo.root / "allow.txt"
        allow.write_text("names\tItems.Take\tdocs/kiln/other.md\tdeliberate\n", encoding="utf-8")
        code, found, _ = self.repo.run("names", allow=allow)
        self.assertEqual((code, len(found["names"])), (1, 1))

    def test_a_missing_reason_or_a_bad_check_is_an_error(self):
        allow = self.repo.root / "allow.txt"
        for text in ("names\tItems.Take\tdocs/*\t\n", "names\tItems.Take\tdocs/*\n", "colors\tx\t*\tr\n"):
            allow.write_text(text, encoding="utf-8")
            code, _, out = self.repo.run("names", allow=allow)
            self.assertEqual(code, 1, text)
            self.assertIn("drift:", out)

    def test_an_entry_of_a_check_that_did_not_run_is_not_stale(self):
        allow = self.repo.root / "allow.txt"
        allow.write_text("versions\tItems.Give\t*\tr\n", encoding="utf-8")
        code, _, _ = self.repo.run("names", allow=allow)
        self.assertEqual(code, 0)


class Cli(Case):
    def test_exit_codes_and_warn(self):
        self.repo.doc("`Items.Take`")
        root = ["--tree", str(self.repo.root), "--allow", str(self.repo.root / "none.txt"), "--wiki", str(self.repo.wiki)]
        with contextlib.redirect_stdout(io.StringIO()) as o:
            self.assertEqual(drift.main(["names"] + root), 1)
            self.assertEqual(drift.main(["names", "--warn", "names"] + root), 0)
        self.assertIn("WARN", o.getvalue())
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(drift.main(["versions", "facts", "map"] + root), 1)   # docs-map names a page the fixture lacks
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(drift.main(["colors"] + root), 2)
            self.assertEqual(drift.main(["--tree", str(self.repo.root / "nowhere")]), 2)

    def test_all_runs_every_check(self):
        with contextlib.redirect_stdout(io.StringIO()) as o:
            drift.main(["--tree", str(self.repo.root), "--allow", str(self.repo.root / "none.txt"), "--wiki", str(self.repo.wiki)])
        for c in drift.CHECKS:
            self.assertIn(f"drift: {c}", o.getvalue())


@unittest.skipUnless(os.environ.get("KILN_DRIFT_REAL") == "1", "KILN_DRIFT_REAL=1 runs the checks over this repository")
class ThisRepository(unittest.TestCase):
    def test_it_is_clean(self):
        out = io.StringIO()
        code, _ = drift.run(HERE.parent.parent, list(drift.CHECKS), set(), None, None, out)
        self.assertEqual(code, 0, out.getvalue())


if __name__ == "__main__":
    unittest.main()
