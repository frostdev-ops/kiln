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
        self.assertIn("allowed, now experimental: Kiln.Chat.Send() -> void", out)
        # the same through Unshipped.txt, as the analyzer writes it
        self.r.commit(self.project(SVC, ["Kiln.Chat.Send() -> void", "Kiln.Chat.X"],
                                   ["*REMOVED*Kiln.Chat.Send() -> void", "[KILNX002]Kiln.Chat.Send() -> void"]))
        code, out = run(self.r, "removed", "v1")
        self.assertEqual(0, code, out)
        self.assertIn("allowed, now experimental", out)

    def test_a_different_line_with_an_experimental_prefix_does_not_excuse_a_removal(self):
        self.r.commit(self.project(API, ["Kiln.A.Go() -> void"]), tag="v1")
        self.r.commit(self.project(API, ["[KILNX001]Kiln.A.Go(int x) -> void"]))
        self.assertEqual(1, run(self.r, "removed", "v1")[0])

    def test_a_bad_revision_is_exit_2(self):
        self.r.commit(self.project(API, ["Kiln.A"]))
        self.assertEqual(2, run(self.r, "removed", "v-none")[0])


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
