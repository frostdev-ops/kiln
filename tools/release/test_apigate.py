#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests for the release's API gate (tools/release/lib/apigate.py, called through tools/release/api-gate.sh): removed
lines are refused and named, additions pass, the first release with the gate and projects not gated yet pass, the
experimental cases, a deleted Shipped.txt, promote's order, *REMOVED* and idempotence, the API version rule and
bump-api (on copies of the three real files).

  python3 -I tools/release/test_apigate.py
Every case builds a throwaway git repository under TMPDIR (tags are made only there) and never touches this one.
"""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
spec = importlib.util.spec_from_file_location("apigate", HERE / "lib" / "apigate.py")
ag = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ag)

API = "managed/Kiln.Api"
GAME = "managed/Kiln.Game"
SVC = "managed/services/Kiln.Chat"
MODS_CS = '    public const string ApiVersion = "0.2.0";\n'
GIT_ENV = {"GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1", "GIT_AUTHOR_NAME": "t",
           "GIT_AUTHOR_EMAIL": "t@example.invalid", "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@example.invalid"}


def api(*entries):
    return "#nullable enable\n" + "".join(e + "\n" for e in entries)


class Repo:
    def __init__(self, root):
        self.root = root
        self.git("init", "-q", "-b", "main")

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.root), *args], check=True, capture_output=True, text=True,
                              env={**os.environ, **GIT_ENV}).stdout

    def put(self, files):
        for rel, text in files.items():
            p = self.root / rel
            if text is None:
                if p.exists():
                    p.unlink()
                continue
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(text, encoding="utf-8")

    def commit(self, files, tag=None):
        self.put(files)
        self.git("add", "-A")
        self.git("commit", "-q", "--allow-empty", "-m", "c")
        if tag:
            self.git("tag", tag)

    def read(self, rel):
        return (self.root / rel).read_text(encoding="utf-8")


def run(repo, *argv):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = ag.main(["--repo", str(repo.root), *argv])
    return code, out.getvalue() + err.getvalue()


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="apigate-")
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.r = Repo(Path(self.tmp))

    def project(self, proj, shipped, unshipped=()):
        return {f"{proj}/PublicAPI.Shipped.txt": api(*shipped), f"{proj}/PublicAPI.Unshipped.txt": api(*unshipped)}


class Removed(Base):
    def test_a_removed_line_fails_and_is_named(self):
        self.r.commit(self.project(API, ["Kiln.A", "Kiln.B.Go() -> void"]), tag="v1")
        self.r.commit(self.project(API, ["Kiln.A"]))
        code, out = run(self.r, "removed", "v1")
        self.assertEqual(1, code, out)
        self.assertIn(f"{API}/PublicAPI.Shipped.txt: removed: Kiln.B.Go() -> void", out)
        self.assertNotIn("removed: Kiln.A\n", out)

    def test_an_added_line_passes(self):
        self.r.commit(self.project(API, ["Kiln.A"]), tag="v1")
        self.r.commit(self.project(API, ["Kiln.A"], ["Kiln.A.New() -> void"]))
        self.assertEqual(0, run(self.r, "removed", "v1")[0])
        self.r.commit(self.project(API, ["Kiln.A", "Kiln.A.New() -> void"]))
        self.assertEqual(0, run(self.r, "removed", "v1")[0])

    def test_the_working_tree_is_the_new_side_with_worktree(self):
        self.r.commit(self.project(API, ["Kiln.A", "Kiln.B"]), tag="v1")
        self.r.put(self.project(API, ["Kiln.A"]))
        self.assertEqual(0, run(self.r, "removed", "v1")[0], "HEAD still has it")
        code, out = run(self.r, "removed", "v1", "--worktree")
        self.assertEqual(1, code)
        self.assertIn("removed: Kiln.B", out)

    def test_nullable_line_is_not_a_removal(self):
        self.r.commit(self.project(API, ["Kiln.A"]), tag="v1")
        self.r.commit({f"{API}/PublicAPI.Shipped.txt": "Kiln.A\n"})
        self.assertEqual(0, run(self.r, "removed", "v1")[0])

    def test_first_release_with_no_files_at_the_old_tag_passes(self):
        self.r.commit({"README": "x"}, tag="v0")
        self.r.commit(self.project(API, ["Kiln.A"]))
        code, out = run(self.r, "removed", "v0")
        self.assertEqual(0, code, out)
        self.assertIn("first release with the gate", out)

    def test_a_project_not_gated_at_the_old_tag_passes_even_when_its_files_are_deleted(self):
        # the typegen package deletes Kiln.Game's files before the first gated release: Kiln.Game was never gated
        self.r.commit(self.project(API, ["Kiln.A"]), tag="v1")
        self.r.commit({**self.project(API, ["Kiln.A"]), **self.project(GAME, ["Kiln.Game.X"])})
        code, out = run(self.r, "removed", "v1")
        self.assertEqual(0, code, out)
        self.assertIn(f"{GAME}: not gated at v1", out)
        self.r.commit({f"{GAME}/PublicAPI.Shipped.txt": None, f"{GAME}/PublicAPI.Unshipped.txt": None})
        self.assertEqual(0, run(self.r, "removed", "v1")[0])

    def test_a_shipped_file_deleted_after_a_tag_that_had_it_fails_and_is_named(self):
        self.r.commit({**self.project(API, ["Kiln.A"]), **self.project(GAME, ["Kiln.Game.X", "Kiln.Game.Y"])}, tag="v1")
        self.r.commit({f"{GAME}/PublicAPI.Shipped.txt": None, f"{GAME}/PublicAPI.Unshipped.txt": None})
        code, out = run(self.r, "removed", "v1")
        self.assertEqual(1, code, out)
        self.assertIn(f"{GAME}/PublicAPI.Shipped.txt: deleted at HEAD: a removal of all its 2 shipped lines", out)
        self.assertIn("removed: Kiln.Game.Y", out)

    def test_lines_still_in_unshipped_at_the_tag_count_as_shipped(self):
        # a release is tagged before promote's moved lines are committed
        self.r.commit(self.project(API, ["Kiln.A"], ["Kiln.A.Added() -> void"]), tag="v1")
        self.r.commit(self.project(API, ["Kiln.A"]))
        code, out = run(self.r, "removed", "v1")
        self.assertEqual(1, code, out)
        self.assertIn("removed: Kiln.A.Added() -> void", out)

    def test_a_removed_entry_in_unshipped_is_a_removal_named_there(self):
        self.r.commit(self.project(API, ["Kiln.A", "Kiln.B"]), tag="v1")
        self.r.commit(self.project(API, ["Kiln.A", "Kiln.B"], ["*REMOVED*Kiln.B"]))
        code, out = run(self.r, "removed", "v1")
        self.assertEqual(1, code, out)
        self.assertIn(f"{API}/PublicAPI.Unshipped.txt: removed: Kiln.B", out)

    def test_an_experimental_line_may_go(self):
        self.r.commit(self.project(API, ["Kiln.A", "[KILNX001]Kiln.Lab.Poke() -> void"]), tag="v1")
        self.r.commit(self.project(API, ["Kiln.A"]))
        self.assertEqual(0, run(self.r, "removed", "v1")[0])

    def test_a_line_that_became_experimental_is_allowed_and_listed(self):
        self.r.commit(self.project(SVC, ["Kiln.Chat.Send() -> void", "Kiln.Chat.X"]), tag="v1")
        self.r.commit(self.project(SVC, ["Kiln.Chat.X", "[KILNX002]Kiln.Chat.Send() -> void"]))
        code, out = run(self.r, "removed", "v1")
        self.assertEqual(0, code, out)
        self.assertIn("allowed (made experimental ([KILNX002])): Kiln.Chat.Send() -> void", out)
        # the same through Unshipped.txt, as the analyzer writes it
        self.r.commit(self.project(SVC, ["Kiln.Chat.Send() -> void", "Kiln.Chat.X"],
                                   ["*REMOVED*Kiln.Chat.Send() -> void", "[KILNX002]Kiln.Chat.Send() -> void"]))
        code, out = run(self.r, "removed", "v1")
        self.assertEqual(0, code, out)
        self.assertIn("allowed (made experimental ([KILNX002]))", out)

    def test_a_different_line_with_an_experimental_prefix_does_not_excuse_a_removal(self):
        self.r.commit(self.project(API, ["Kiln.A.Go() -> void"]), tag="v1")
        self.r.commit(self.project(API, ["[KILNX001]Kiln.A.Go(int x) -> void"]))
        self.assertEqual(1, run(self.r, "removed", "v1")[0])

    def test_a_bad_revision_is_exit_2(self):
        self.r.commit(self.project(API, ["Kiln.A"]))
        self.assertEqual(2, run(self.r, "removed", "v-none")[0])


FWD = " (forwarded, contained in Kiln.Api)"


class Forwarded(Base):
    """A type moved to another assembly with a type forward (identity moved Kiln.ItemId from Kiln.Items to Kiln.Api)."""

    def moved(self, api_records=True, items_extra=(), api_extra=()):
        files = {**self.project(SVC, ["Kiln.ItemId", "Kiln.ItemId.Value.get -> uint", "Kiln.Items.X"],
                                ["*REMOVED*Kiln.ItemId", "*REMOVED*Kiln.ItemId.Value.get -> uint",
                                 "Kiln.ItemId" + FWD, "Kiln.ItemId.Value.get -> uint" + FWD, *items_extra]),
                 **self.project(API, ["Kiln.A"], (["Kiln.ItemId", "Kiln.ItemId.Value.get -> uint"] if api_records else [])
                                + list(api_extra)),
                 f"{SVC}/Kiln.Chat.csproj": "<Project />\n", f"{API}/Kiln.Api.csproj": "<Project />\n"}
        return files

    def test_removed_lists_a_forwarded_pair_as_allowed(self):
        self.r.commit(self.project(SVC, ["Kiln.ItemId", "Kiln.ItemId.Value.get -> uint", "Kiln.Items.X"]), tag="v1")
        self.r.commit(self.moved())
        code, out = run(self.r, "removed", "v1")
        self.assertEqual(0, code, out)
        self.assertIn(f"{SVC}/PublicAPI.Shipped.txt: allowed (forwarded to Kiln.Api): Kiln.ItemId.Value.get -> uint", out)

    def test_record_and_promote_allow_a_forwarded_pair(self):
        self.r.commit(self.moved())
        code, out = run(self.r, "record")
        self.assertEqual(0, code, out)
        self.assertIn("allowed (forwarded to Kiln.Api): *REMOVED*Kiln.ItemId", out)
        self.assertEqual(0, run(self.r, "promote")[0])
        # the forwarded lines stand in Shipped.txt in place of the old ones: the record the analyzer wants
        self.assertEqual(api("Kiln.ItemId" + FWD, "Kiln.ItemId.Value.get -> uint" + FWD, "Kiln.Items.X"),
                         self.r.read(f"{SVC}/PublicAPI.Shipped.txt"))
        self.r.commit({})
        self.assertEqual(0, run(self.r, "record")[0])

    def test_a_forward_to_nowhere_is_refused_everywhere(self):
        # Kiln.Api has a PublicAPI record, and it does not record the moved type
        self.r.commit(self.project(SVC, ["Kiln.ItemId", "Kiln.ItemId.Value.get -> uint", "Kiln.Items.X"]), tag="v1")
        files = self.moved(api_records=False)
        self.r.commit(files)
        code, out = run(self.r, "removed", "v1")
        self.assertEqual(1, code, out)
        self.assertIn("forward to nowhere", out)
        code, out = run(self.r, "record")
        self.assertEqual(1, code, out)
        self.assertIn(f"{SVC}/PublicAPI.Unshipped.txt: *REMOVED*Kiln.ItemId: forwarded to Kiln.Api, whose PublicAPI "
                      "record does not have it", out)
        code, out = run(self.r, "promote")
        self.assertEqual(1, code, out)
        for p, text in files.items():
            self.assertEqual(text, self.r.read(p), p)

    def test_the_target_assembly_is_found_by_its_assembly_name(self):
        files = self.moved(api_records=False)
        files[f"managed/Other/Renamed.csproj"] = "<Project><PropertyGroup><AssemblyName>Kiln.Api</AssemblyName>" \
                                                 "</PropertyGroup></Project>\n"
        files.update(self.project("managed/Other", ["Kiln.ItemId", "Kiln.ItemId.Value.get -> uint"]))
        files[f"{API}/Kiln.Api.csproj"] = "<Project><PropertyGroup><AssemblyName>Kiln.Old</AssemblyName></PropertyGroup></Project>\n"
        self.r.commit(files)
        code, out = run(self.r, "record")
        self.assertEqual(0, code, out)
        self.assertIn("allowed (forwarded to Kiln.Api): *REMOVED*Kiln.ItemId\n", out)
        # the project named Kiln.Api builds Kiln.Old: its record (which has the lines) is not the target's
        files.update(self.project("managed/Other", []))
        files.update(self.project(API, ["Kiln.A", "Kiln.ItemId", "Kiln.ItemId.Value.get -> uint"]))
        self.r.commit(files)
        self.assertEqual(1, run(self.r, "record")[0])

    def test_a_forward_to_an_assembly_without_a_record_is_allowed_and_said(self):
        files = self.moved()
        for k in list(files):
            files[k] = files[k].replace("Kiln.Api)", "System.Runtime)")
        self.r.commit(files)
        code, out = run(self.r, "record")
        self.assertEqual(0, code, out)
        self.assertIn("forwarded to System.Runtime (no PublicAPI record of System.Runtime to check)", out)

    def test_a_forwarded_line_for_another_symbol_does_not_cover(self):
        self.r.commit({**self.project(SVC, ["Kiln.ItemId", "Kiln.Other"], ["*REMOVED*Kiln.Other", "Kiln.ItemId" + FWD,
                                                                          "*REMOVED*Kiln.ItemId"]),
                       **self.project(API, ["Kiln.ItemId"]), f"{API}/Kiln.Api.csproj": "<Project />\n"})
        code, out = run(self.r, "record")
        self.assertEqual(1, code, out)
        self.assertIn("*REMOVED*Kiln.Other: a shipped line may not be removed", out)
        self.assertNotIn("*REMOVED*Kiln.ItemId:", out)


class Record(Base):
    def test_a_bare_removed_line_fails_and_is_named(self):
        self.r.commit({**self.project(API, ["Kiln.A", "Kiln.B"], ["*REMOVED*Kiln.B"]), **self.project(SVC, ["Kiln.Chat"])})
        code, out = run(self.r, "record")
        self.assertEqual(1, code, out)
        self.assertIn(f"{API}/PublicAPI.Unshipped.txt: *REMOVED*Kiln.B: a shipped line may not be removed", out)

    def test_an_experimental_pair_and_a_lab_line_pass(self):
        self.r.commit(self.project(API, ["Kiln.A", "Kiln.B", "[KILNX001]Kiln.Lab"],
                                   ["*REMOVED*Kiln.B", "[KILNX004]Kiln.B", "*REMOVED*[KILNX001]Kiln.Lab"]))
        code, out = run(self.r, "record")
        self.assertEqual(0, code, out)
        self.assertIn("allowed (made experimental ([KILNX004])): *REMOVED*Kiln.B", out)
        self.assertIn("allowed (lab member removed", out)

    def test_the_pair_must_be_in_the_same_file(self):
        # an experimental line for the symbol in another project's Unshipped.txt does not cover the removal
        self.r.commit({**self.project(API, ["Kiln.A", "Kiln.B"], ["*REMOVED*Kiln.B"]),
                       **self.project(SVC, ["Kiln.Chat"], ["[KILNX004]Kiln.B"])})
        self.assertEqual(1, run(self.r, "record")[0])

    def test_a_merge_conflict_marker_fails_and_is_named(self):
        for marker in ("<<<<<<< HEAD", "||||||| dfff2e51", "=======", ">>>>>>> dev25/identity"):
            self.r.commit(self.project(API, ["Kiln.A", "Kiln.B"], ["Kiln.C", marker, "Kiln.D"]))
            code, out = run(self.r, "record")
            self.assertEqual(1, code, marker)
            self.assertIn(f"{API}/PublicAPI.Unshipped.txt: line 3: {marker}: a merge conflict marker", out)
        self.r.commit({f"{API}/PublicAPI.Shipped.txt": api("Kiln.A", "=======", "Kiln.B"),
                       f"{API}/PublicAPI.Unshipped.txt": api()})
        code, out = run(self.r, "record")
        self.assertEqual(1, code)
        self.assertIn(f"{API}/PublicAPI.Shipped.txt: line 3: =======", out)
        self.r.commit({f"{API}/PublicAPI.Shipped.txt": api("Kiln.A", "Kiln.Equals(string ======= x) -> bool")})
        self.assertEqual(0, run(self.r, "record")[0], "a marker only at the start of a line")

    def test_reads_a_revision_or_the_working_tree(self):
        self.r.commit(self.project(API, ["Kiln.A", "Kiln.B"]))
        self.r.put(self.project(API, ["Kiln.A", "Kiln.B"], ["*REMOVED*Kiln.B"]))
        self.assertEqual(0, run(self.r, "record")[0], "HEAD has no *REMOVED* line")
        code, out = run(self.r, "record", "--worktree")
        self.assertEqual(1, code)
        self.assertIn("*REMOVED*Kiln.B", out)


class Pending(Base):
    def test_lists_only_unshipped_files_with_lines(self):
        self.r.commit({**self.project(API, ["Kiln.A"], ["Kiln.A.B", "[KILNX001]Kiln.A.C", "*REMOVED*Kiln.A"]),
                       **self.project(SVC, ["Kiln.Chat"])})
        code, out = run(self.r, "pending")
        self.assertEqual(0, code)
        self.assertIn(f"{API}/PublicAPI.Unshipped.txt: 3 lines (1 added, 1 experimental, 1 *REMOVED*)", out)
        self.assertNotIn(SVC, out)
        self.r.put(self.project(API, ["Kiln.A"]))
        self.assertIn("no pending API", run(self.r, "pending", "--worktree")[1])


class VersionRule(Base):
    def files(self, version, unshipped=()):
        return {**self.project(API, ["Kiln.A"], unshipped), f"{API}/Mods.cs": MODS_CS.replace("0.2.0", version)}

    def test_new_api_with_the_same_version_is_refused(self):
        self.r.commit(self.files("0.2.0"), tag="v1")
        self.r.commit(self.files("0.2.0", ["Kiln.A.New() -> void"]))
        code, out = run(self.r, "version-rule", "v1")
        self.assertEqual(1, code, out)
        self.assertIn("still 0.2.0", out)
        self.assertIn("bump-api", out)

    def test_already_released_unshipped_lines_need_no_raise(self):
        self.r.commit(self.files("0.2.0", ["Kiln.A.New() -> void"]), tag="v1")
        self.r.commit(self.files("0.2.0", ["Kiln.A.New() -> void"]))
        code, out = run(self.r, "version-rule", "v1")
        self.assertEqual(0, code, out)
        self.assertIn("no new API", out)

    def test_promoting_new_api_before_release_does_not_bypass_the_raise(self):
        self.r.commit(self.files("0.2.0"), tag="v1")
        self.r.put(self.files("0.2.0", ["Kiln.A.New() -> void"]))
        self.assertEqual(0, run(self.r, "promote")[0])
        self.r.commit({})
        code, out = run(self.r, "version-rule", "v1")
        self.assertEqual(1, code, out)
        self.assertIn("1 new API lines", out)
        self.assertNotIn("run tools/release/api-gate.sh promote", out)

    def test_new_api_is_compared_per_project(self):
        self.r.commit({**self.files("0.2.0"), **self.project(SVC, ["Kiln.Shared"])}, tag="v1")
        self.r.commit(self.project(API, ["Kiln.A", "Kiln.Shared"]))
        code, out = run(self.r, "version-rule", "v1")
        self.assertEqual(1, code, out)
        self.assertIn(f"{API}/PublicAPI.Shipped.txt: 1 added", out)

    def test_a_project_without_an_old_record_counts_only_its_unshipped_additions(self):
        self.r.commit({f"{API}/Mods.cs": MODS_CS}, tag="v1")
        self.r.commit(self.project(API, ["Kiln.A"]))
        self.assertEqual(0, run(self.r, "version-rule", "v1")[0])
        self.r.commit(self.project(API, ["Kiln.A"], ["Kiln.A.New() -> void", "[KILNX001]Kiln.Lab"]))
        code, out = run(self.r, "version-rule", "v1")
        self.assertEqual(1, code, out)
        self.assertIn(f"{API}/PublicAPI.Unshipped.txt: 1 added", out)

    def test_new_api_with_a_higher_version_passes_whatever_part_rose(self):
        self.r.commit(self.files("0.2.0"), tag="v1")
        for v in ("0.2.1", "0.3.0", "1.0.0"):
            self.r.commit(self.files(v, ["Kiln.A.New() -> void"]))
            self.assertEqual(0, run(self.r, "version-rule", "v1")[0], v)

    def test_a_lower_version_is_refused(self):
        self.r.commit(self.files("0.3.0"), tag="v1")
        self.r.commit(self.files("0.2.0", ["Kiln.A.New() -> void"]))
        self.assertEqual(1, run(self.r, "version-rule", "v1")[0])

    def test_no_new_api_or_only_experimental_api_needs_no_raise(self):
        self.r.commit(self.files("0.2.0"), tag="v1")
        self.r.commit(self.files("0.2.0"))
        self.assertEqual(0, run(self.r, "version-rule", "v1")[0])
        self.r.commit(self.files("0.2.0", ["[KILNX001]Kiln.A.Lab() -> void"]))
        self.assertEqual(0, run(self.r, "version-rule", "v1")[0])

    def test_an_old_tag_without_a_readable_mods_cs_warns_and_passes(self):
        self.r.commit(self.project(API, ["Kiln.A"]), tag="v1")
        self.r.commit(self.files("0.2.0", ["Kiln.A.New() -> void"]))
        code, out = run(self.r, "version-rule", "v1")
        self.assertEqual(0, code, out)
        self.assertIn("WARNING: cannot read the API version", out)

    def test_api_version_reads_a_revision(self):
        self.r.commit(self.files("0.2.0"), tag="v1")
        self.r.commit(self.files("0.4.0"))
        self.assertEqual((0, "0.2.0\n"), run(self.r, "api-version", "v1"))
        self.assertEqual((0, "0.4.0\n"), run(self.r, "api-version"))


class Promote(Base):
    def test_additions_are_sorted_ordinally_with_nullable_first(self):
        self.r.commit(self.project(API, ["Kiln.B", "Kiln.a", "const Kiln.K = \"\uff21\" -> string!"],
                                   ["~Kiln.Old.F -> object", "Kiln.A", "Kiln.Z", "[KILNX001]Kiln.Lab", "Kiln.B.C",
                                    "const Kiln.K = \"\U0001f525\" -> string!"]))
        code, out = run(self.r, "promote")
        self.assertEqual(0, code, out)
        # ordinal: "K" < "[" < "a" < "c" < "~", and by UTF-16 code units a surrogate pair (D83D) before U+FF21
        self.assertEqual(api("Kiln.A", "Kiln.B", "Kiln.B.C", "Kiln.Z", "Kiln.a", "[KILNX001]Kiln.Lab",
                             "const Kiln.K = \"\U0001f525\" -> string!", "const Kiln.K = \"\uff21\" -> string!",
                             "~Kiln.Old.F -> object"),
                         self.r.read(f"{API}/PublicAPI.Shipped.txt"))
        self.assertEqual("#nullable enable\n", self.r.read(f"{API}/PublicAPI.Unshipped.txt"))

    def test_running_it_twice_changes_nothing(self):
        self.r.commit(self.project(API, ["Kiln.A"], ["Kiln.C", "Kiln.B"]))
        self.assertEqual(0, run(self.r, "promote")[0])
        first = {p: self.r.read(p) for p in (f"{API}/PublicAPI.Shipped.txt", f"{API}/PublicAPI.Unshipped.txt")}
        code, out = run(self.r, "promote")
        self.assertEqual(0, code)
        self.assertIn("nothing pending", out)
        self.assertEqual(first, {p: self.r.read(p) for p in first})

    def test_an_already_shipped_addition_is_not_doubled(self):
        self.r.commit(self.project(API, ["Kiln.A"], ["Kiln.A"]))
        self.assertEqual(0, run(self.r, "promote")[0])
        self.assertEqual(api("Kiln.A"), self.r.read(f"{API}/PublicAPI.Shipped.txt"))

    def test_an_experimental_line_elsewhere_does_not_let_promote_remove(self):
        # the experimental line is already in Shipped.txt, not beside the *REMOVED* entry in Unshipped.txt
        self.r.commit(self.project(API, ["Kiln.A", "Kiln.B", "[KILNX003]Kiln.B"], ["*REMOVED*Kiln.B"]))
        code, out = run(self.r, "promote")
        self.assertEqual(1, code, out)
        self.assertIn("*REMOVED*Kiln.B: a shipped line may not be removed", out)

    def test_removed_of_a_line_now_experimental_deletes_it(self):
        self.r.commit(self.project(API, ["Kiln.A", "Kiln.B"], ["*REMOVED*Kiln.B", "[KILNX003]Kiln.B"]))
        code, out = run(self.r, "promote")
        self.assertEqual(0, code, out)
        self.assertEqual(api("Kiln.A", "[KILNX003]Kiln.B"), self.r.read(f"{API}/PublicAPI.Shipped.txt"))

    def test_removed_of_an_experimental_line_deletes_it(self):
        self.r.commit(self.project(API, ["Kiln.A", "[KILNX003]Kiln.Lab"], ["*REMOVED*[KILNX003]Kiln.Lab"]))
        self.assertEqual(0, run(self.r, "promote")[0])
        self.assertEqual(api("Kiln.A"), self.r.read(f"{API}/PublicAPI.Shipped.txt"))

    def test_removed_of_a_shipped_line_is_refused_and_nothing_is_written(self):
        files = {**self.project(SVC, ["Kiln.Chat"], ["Kiln.Chat.New"]),
                 **self.project(API, ["Kiln.A", "Kiln.B"], ["*REMOVED*Kiln.B"])}
        self.r.commit(files)
        code, out = run(self.r, "promote")
        self.assertEqual(1, code, out)
        self.assertIn("*REMOVED*Kiln.B: a shipped line may not be removed", out)
        for p, text in files.items():
            self.assertEqual(text, self.r.read(p), p)

    def test_if_same_as_skips_when_the_working_tree_differs(self):
        self.r.commit(self.project(API, ["Kiln.A"], ["Kiln.B"]), tag="v2")
        self.r.put({f"{API}/PublicAPI.Unshipped.txt": api("Kiln.B", "Kiln.C")})
        code, out = run(self.r, "promote", "--if-same-as", "v2")
        self.assertEqual(3, code, out)
        self.assertIn(f"{API}/PublicAPI.Unshipped.txt", out)
        self.assertEqual(api("Kiln.A"), self.r.read(f"{API}/PublicAPI.Shipped.txt"))
        self.r.put({f"{API}/PublicAPI.Unshipped.txt": api("Kiln.B")})
        self.assertEqual(0, run(self.r, "promote", "--if-same-as", "v2")[0])
        self.assertEqual(api("Kiln.A", "Kiln.B"), self.r.read(f"{API}/PublicAPI.Shipped.txt"))


class BumpApi(Base):
    THREE = ("managed/Kiln.Api/Mods.cs", "managed/Kiln.Sdk/Sdk/Kiln.Version.props", "crates/kiln-manage/src/profile.rs")

    def setUp(self):
        super().setUp()
        for rel in self.THREE:  # copies of the real files: the edits must fit them as they are
            (self.r.root / rel).parent.mkdir(parents=True, exist_ok=True)
            shutil.copy(REPO / rel, self.r.root / rel)
        self.r.put({"docs/kiln/modding/manifest.md": "Today's API: `Kiln.Mods.ApiVersion` = 0.2.0.\nSET 0.2.0-dev\n",
                    "docs/kiln/release-notes/x.md": "mod API 0.2.0\n"})
        self.r.commit({})

    def state(self):
        mods, props, prof = (self.r.read(p) for p in self.THREE)
        return (ag.CONST_RE.search(mods).group(2), ag.PROPS_RE.search(props).group(2), ag.PROFILE_RE.search(prof).group(2))

    def test_the_real_files_are_found(self):
        self.assertEqual(("0.2.0", "0.2", "0.2.0"), self.state())

    def test_default_is_the_next_minor_and_doc_lines_are_printed(self):
        before = {p: self.r.read(p) for p in self.THREE}
        code, out = run(self.r, "bump-api")
        self.assertEqual(0, code, out)
        self.assertEqual(("0.3.0", "0.3", "0.3.0"), self.state())
        self.assertIn("docs/kiln/modding/manifest.md:1:", out)
        self.assertNotIn("release-notes", out)
        self.assertNotIn("SET 0.2.0-dev", out)
        # only the version text changed in each file
        for p in self.THREE:
            diff = [(a, b) for a, b in zip(before[p].splitlines(), self.r.read(p).splitlines()) if a != b]
            self.assertEqual(1, len(diff), p)
        # what the other readers parse still matches (tools/package/stage.sh, crates/kiln-manage/tests/profiles.rs)
        self.assertEqual("0.3.0", ag.API_RE.search(self.r.read(self.THREE[0])).group(1))

    def test_bump_keeps_both_gates_sound_for_two_releases(self):
        for carrier in (ag.SHIPPED, ag.UNSHIPPED):
            with self.subTest(carrier=carrier):
                self.r.put({p: (REPO / p).read_text(encoding="utf-8") for p in self.THREE})
                const = 'const Kiln.Mods.ApiVersion = "0.2.0" -> string!'
                self.r.commit(self.project(API, ["Kiln.A", const] if carrier == ag.SHIPPED else ["Kiln.A"],
                                           [const] if carrier == ag.UNSHIPPED else []))
                for release, version in enumerate(("0.3.0", "0.4.0"), 1):
                    tag = f"{carrier}-v{release}"
                    self.r.git("tag", tag)
                    entries = ag.lines(self.r.read(f"{API}/{ag.UNSHIPPED}"))
                    self.r.put({f"{API}/{ag.UNSHIPPED}": api(*entries, f"Kiln.A.New{release}() -> void")})
                    code, out = run(self.r, "bump-api")
                    self.assertEqual(0, code, out)
                    self.assertIn(f'const Kiln.Mods.ApiVersion = "{version}" -> string!',
                                  self.r.read(f"{API}/{carrier if release == 1 else ag.SHIPPED}"))
                    self.r.commit({})
                    for args in (("removed", tag), ("record",), ("version-rule", tag), ("promote",)):
                        code, out = run(self.r, *args)
                        self.assertEqual(0, code, f"{args}: {out}")
                    self.r.commit({})
                    for args in (("removed", tag), ("record",)):
                        code, out = run(self.r, *args)
                        self.assertEqual(0, code, f"after promote {args}: {out}")

    def test_const_value_replacement_is_allowed_in_all_record_commands(self):
        old = 'const Kiln.Mods.ApiVersion = "0.2.0" -> string!'
        new = 'const Kiln.Mods.ApiVersion = "0.3.0" -> string!'
        self.r.commit(self.project(API, [old]), tag="v1")
        self.r.commit(self.project(API, [old], ["*REMOVED*" + old, new]))
        for args in (("removed", "v1"), ("record",), ("promote",)):
            code, out = run(self.r, *args)
            self.assertEqual(0, code, f"{args}: {out}")
        self.assertEqual(api(new), self.r.read(f"{API}/{ag.SHIPPED}"))

    def test_only_the_exact_api_version_const_value_may_change(self):
        old = 'const Kiln.Mods.ApiVersion = "0.2.0" -> string!'
        for new in ('const Kiln.Mods.ApiVersion = "0.3.0" -> string',
                    'const Kiln.Mods.ApiVersion = "0.3.0" -> object!',
                    'const Kiln.Other.ApiVersion = "0.3.0" -> string!',
                    'static Kiln.Mods.ApiVersion.get -> string!', ""):
            with self.subTest(new=new):
                self.r.commit(self.project(API, [old], ["*REMOVED*" + old, new]))
                for args in (("record",), ("promote",)):
                    code, out = run(self.r, *args)
                    self.assertEqual(1, code, f"{args}: {out}")

    def test_bump_preserves_other_const_lines_and_line_endings(self):
        old = 'const Kiln.Mods.ApiVersion = "0.2.0" -> string!'
        other = 'const Kiln.Other.Version = "0.2.0" -> string!'
        p = self.r.root / API / ag.SHIPPED
        p.write_bytes(api(old, other).replace("\n", "\r\n").encode())
        self.assertEqual(0, run(self.r, "bump-api")[0])
        self.assertEqual(api(old.replace('"0.2.0"', '"0.3.0"'), other).replace("\n", "\r\n").encode(),
                         p.read_bytes())

    def test_an_explicit_version_and_a_patch_version(self):
        self.assertEqual(0, run(self.r, "bump-api", "0.2.1")[0])
        self.assertEqual(("0.2.1", "0.2.1", "0.2.1"), self.state())
        self.assertEqual(0, run(self.r, "bump-api", "0.5.0")[0])
        self.assertEqual(("0.5.0", "0.5", "0.5.0"), self.state())

    def test_refuses_a_version_that_is_not_higher(self):
        for v in ("0.2.0", "0.1.9"):
            code, out = run(self.r, "bump-api", v)
            self.assertEqual(1, code, v)
            self.assertIn("not higher", out)
        self.assertEqual(2, run(self.r, "bump-api", "0.3.0-dev.1")[0])
        self.assertEqual(("0.2.0", "0.2", "0.2.0"), self.state())

    def test_a_new_major_needs_major(self):
        code, out = run(self.r, "bump-api", "1.0.0")
        self.assertEqual(1, code)
        self.assertIn("--major", out)
        self.assertEqual(("0.2.0", "0.2", "0.2.0"), self.state())
        self.assertEqual(0, run(self.r, "bump-api", "--major")[0])
        self.assertEqual(("1.0.0", "1.0", "1.0.0"), self.state())

    def test_refuses_when_the_three_places_disagree(self):
        p = self.r.root / self.THREE[2]
        p.write_text(p.read_text(encoding="utf-8").replace('KILN_API: &str = "0.2.0"', 'KILN_API: &str = "0.1.0"'),
                     encoding="utf-8")
        code, out = run(self.r, "bump-api")
        self.assertEqual(1, code)
        self.assertIn("disagree", out)
        self.assertEqual("0.2.0", ag.CONST_RE.search(self.r.read(self.THREE[0])).group(2))


class Script(Base):
    def test_api_gate_sh_runs_the_tool(self):
        self.r.commit(self.project(API, ["Kiln.A"]), tag="v1")
        self.r.commit(self.project(API, []))
        r = subprocess.run([str(HERE / "api-gate.sh"), "removed", "v1"], capture_output=True, text=True,
                           env={**os.environ, **GIT_ENV, "KILN_API_GATE_REPO": str(self.r.root)})
        self.assertEqual(1, r.returncode, r.stdout + r.stderr)
        self.assertIn("removed: Kiln.A", r.stderr)


if __name__ == "__main__":
    unittest.main()
