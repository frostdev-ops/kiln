#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests for the runtime plugin check (tools/release/lib/plugins.py) and its wiring: every compatibility plugin is
expected, a missing one or one in a non-default component fails, stage.sh is strict and release.sh/checks.sh run the
check; Enshwick's loader build refuses a checkout that is not the pinned commit.

  python3 -I tools/release/test_plugins.py
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import zlib

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
spec = importlib.util.spec_from_file_location("plugins", HERE / "lib" / "plugins.py")
pl = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pl)
spec = importlib.util.spec_from_file_location("notices", HERE / "lib" / "notices.py")
notices = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notices)

BOTH = ["server", "client"]
COMPONENTS = {
    "core": {"sides": BOTH, "default": True},
    "emf": {"sides": BOTH, "default": True, "requires": ["core"]},
    "eml": {"sides": BOTH, "default": True, "requires": ["core"]},
    "shroudforge": {"sides": BOTH, "default": True, "requires": ["core"]},
    "enshwick": {"sides": BOTH, "default": True, "requires": ["core"]},
    "samples": {"sides": ["server"], "default": False, "requires": ["core"]},
}


def runtime(path, exp, drop=(), components=None, sizes=None):
    files = [{"path": p, "component": c, "size": (sizes or {}).get(p, 4), "sha256": hashlib.sha256(b"MZ..").hexdigest()}
             for p, c in exp if p not in drop]
    m = {"format": 1, "components": components or COMPONENTS, "files": files}
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("kiln-runtime.json", json.dumps(m))
    return str(path)


class ExpectedTests(unittest.TestCase):
    def test_uninitialized_shroudforge_is_still_required(self):
        with tempfile.TemporaryDirectory() as d:
            src = Path(d)
            adapter = src / "native/plugins/shroudforge/CMakeLists.txt"
            adapter.parent.mkdir(parents=True)
            adapter.write_text("kiln_plugin(shroudforge src/plugin.cpp)\n")
            exp = dict(pl.expected(d))
            self.assertIn("kiln/plugins/shroudforge/shroudforge-runtime.dll", exp)
            rt = runtime(src / "rt.zip", list(exp.items()))
            self.assertEqual(pl.main(["check", "--src", d, rt]), 1)

    def test_every_compatibility_plugin_is_expected(self):
        exp = dict(pl.expected(str(ROOT)))
        for p in ("greeneye", "flame", "shroudtopia", "set", "enshwick", "shroudlink", "ember", "shroudforge"):
            self.assertEqual(exp.get(f"kiln/plugins/kiln-{p}.dll"), "core", p)
        self.assertEqual(exp["kiln/plugins/kiln-emf.dll"], "emf")
        self.assertEqual(exp["kiln/plugins/kiln-eml.dll"], "eml")
        self.assertEqual(exp["kiln/plugins/shroudforge/shroudforge-runtime.dll"], "shroudforge")
        self.assertEqual(exp["kiln/plugins/shroudforge/kfc-runtime.dll"], "shroudforge")
        self.assertEqual(exp["kiln/plugins/enshwick/enshwick.dll"], "enshwick")
        self.assertEqual(exp["kiln/plugins/enshwick/LICENSE.txt"], "enshwick")
        # compat is the plugins' shared code and host tests: no DLL of its own
        self.assertNotIn("kiln/plugins/kiln-compat.dll", exp)
        # a plugin folder added later is expected without editing the list, unless it opts out explicitly
        for d in (ROOT / "native" / "plugins").iterdir():
            if (d / "CMakeLists.txt").is_file() and pl.NO_PLUGIN not in (d / "CMakeLists.txt").read_text():
                self.assertIn(f"kiln/plugins/kiln-{d.name}.dll", exp)


class CheckTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="kiln-plugins-test-")
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)
        self.exp = pl.expected(str(ROOT))

    def check(self, rt):
        return pl.main(["check", "--src", str(ROOT), rt])

    def test_complete_runtime_passes(self):
        self.assertEqual(self.check(runtime(self.dir / "rt.zip", self.exp)), 0)

    def test_missing_plugin_fails_by_name(self):
        rt = runtime(self.dir / "rt.zip", self.exp, drop={"kiln/plugins/kiln-shroudlink.dll"})
        components, files = pl.from_zip(rt)
        bad = pl.problems(self.exp, components, files)
        self.assertEqual(bad, ["kiln/plugins/kiln-shroudlink.dll: missing (expected in component core)"])
        self.assertEqual(self.check(rt), 1)
        rt = runtime(self.dir / "rt2.zip", self.exp, drop={"kiln/plugins/enshwick/enshwick.dll"})
        self.assertEqual(self.check(rt), 1)

    def test_non_default_or_one_sided_component_fails(self):
        comps = json.loads(json.dumps(COMPONENTS))
        comps["emf"]["default"] = False
        comps["enshwick"]["sides"] = ["client"]
        components, files = pl.from_zip(runtime(self.dir / "rt.zip", self.exp, components=comps))
        bad = pl.problems(self.exp, components, files)
        self.assertIn("kiln/plugins/kiln-emf.dll: component emf is not installed by default", bad)
        self.assertIn("kiln/plugins/enshwick/enshwick.dll: component enshwick is not for server", bad)

    def test_wrong_component_and_empty_file_fail(self):
        exp = [(p, "core" if p.endswith("enshwick.dll") else c) for p, c in self.exp]
        components, files = pl.from_zip(runtime(self.dir / "rt.zip", exp, sizes={"kiln/plugins/kiln-flame.dll": 0}))
        bad = pl.problems(self.exp, components, files)
        self.assertIn("kiln/plugins/enshwick/enshwick.dll: in component core, expected enshwick", bad)
        self.assertIn("kiln/plugins/kiln-flame.dll: empty", bad)

    def test_stage_folder(self):
        stage = self.dir / "stage"
        for p, c in self.exp:
            f = stage / c / p
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_bytes(b"MZ..")
        (stage / "kiln-runtime.json").write_text(json.dumps({"format": 1, "components": COMPONENTS}))
        self.assertEqual(self.check(str(stage)), 0)
        (stage / "core" / "kiln/plugins/kiln-greeneye.dll").unlink()
        self.assertEqual(self.check(str(stage)), 1)


class WiringTests(unittest.TestCase):
    def test_native_service_copy_errors_are_not_swallowed(self):
        script = (ROOT / "tools/package/stage.sh").read_text()
        start = script.index("rsync -a --include='*.dll' --include='*.pdb' --exclude='*' \"$bin/services/\"")
        command = script[start:script.index("\nrsync -a --include='Kiln.*'", start)]
        r = subprocess.run(["bash", "-c", 'set -euo pipefail; bin=/fixture; k=/stage; '
                            'rsync() { return 23; }; miss() { echo "$*" >&2; exit 1; };\n' + command],
                           capture_output=True, text=True)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("native services", r.stderr)

    def test_successful_builds_must_produce_every_required_output(self):
        # Exercise only stage's output checks; no stage, compiler, network or release entry point is run.
        script = (ROOT / "tools/package/stage.sh").read_text()
        checks = script.split("# every plugin the bundle must hold", 1)[1].split("dotnet_root=", 1)[0]
        checks = "# every plugin the bundle must hold" + checks
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for name in ("native/services/foo/CMakeLists.txt", "managed/services/Kiln.Foo/Kiln.Foo.csproj"):
                f = root / "repo" / name
                f.parent.mkdir(parents=True, exist_ok=True)
                f.touch()
            required = ("cmake/bin/plugins/kiln-eml.dll", "host/Kiln.Eml.dll", "cmake/bin/services/kiln-foo.dll",
                        "host/Kiln.Foo.dll", "host/Kiln.Game.dll", "host/Kiln.Api.dll",
                        "mods/kiln-admin/Kiln.Admin.dll", "mods/kiln-admin/kiln.mod.json",
                        "mods/hello/Hello.dll", "mods/hello/kiln.mod.json", "cmake/bin/kiln-native-hello.dll",
                        "host/game/server-0123456789ab/Kiln.Game.dll")  # one per verified game build (typegen)
            for name in required:
                f = root / "out" / name
                f.parent.mkdir(parents=True, exist_ok=True)
                f.write_bytes(b"fixture")
            def run():
                return subprocess.run(["bash", "-c", 'set -euo pipefail; repo=$1; out=$2; bin=$out/cmake/bin; '
                                       'miss() { echo "$*" >&2; exit 1; };\n' + checks,
                                       "test", str(root / "repo"), str(root / "out")], capture_output=True, text=True)
            self.assertEqual(run().returncode, 0)
            for name in required:
                with self.subTest(missing=name):
                    f = root / "out" / name
                    f.unlink()
                    r = run()
                    self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
                    # the per-build Kiln.Game check is a glob: its message names the pattern, not the file
                    want = "host/game/<side>-<sha12>/Kiln.Game.dll" if name.startswith("host/game/") else str(f)
                    self.assertIn(want, r.stderr)
                    f.write_bytes(b"fixture")

    def test_release_requires_enshwick_notice_provenance(self):
        script = (HERE / "release.sh").read_text()
        checks = "ew_version=" + script.split("\new_version=", 1)[1].split('\npython3 "$lib/notices.py"', 1)[0]
        with tempfile.TemporaryDirectory() as d:
            r = subprocess.run(["bash", "-c", 'set -euo pipefail; stage=$1; KILN_OUT=$1/out; nuget_args=(); '
                                'die() { echo "$*" >&2; exit 1; };\n' + checks, "test", d],
                               capture_output=True, text=True)
            self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
            self.assertIn("Enshwick", r.stderr)

    def test_stage_is_strict_and_defaults(self):
        s = (ROOT / "tools/package/stage.sh").read_text()
        self.assertNotIn('warn "native plugin', s)
        self.assertIn('miss "native plugin kiln-$p failed to build', s)
        self.assertIn("KILN_RELEASE_BUILD", s)
        self.assertIn("tools/release/lib/plugins.py\" check", s)
        self.assertRegex(s, r'"emf": \{"sides": \["server", "client"\], "default": True')
        self.assertRegex(s, r'"enshwick": \{"sides": \["server", "client"\], "default": True')
        self.assertIn("build-runtime.sh", s)

    def test_release_runs_the_check(self):
        self.assertIn('"$lib/plugins.py" check --src "$src" "$runtime_zip"', (HERE / "release.sh").read_text())
        self.assertIn('"$lib/plugins.py" check --src "$src"', (HERE / "checks.sh").read_text())
        self.assertNotIn("--allow-missing", (HERE / "release.sh").read_text())
        self.assertIn("KILN_RELEASE_BUILD=1", (HERE / "release.sh").read_text())

    def test_release_refuses_lenient_stage_before_any_build(self):
        with tempfile.TemporaryDirectory() as d:
            r = subprocess.run(["bash", str(ROOT / "tools/package/stage.sh"), d + "/stage", "--allow-missing"],
                               env={**os.environ, "KILN_RELEASE_BUILD": "1", "KILN_OUT": d + "/out"},
                               capture_output=True, text=True, timeout=10)
            self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
            self.assertIn("never a release", r.stderr)
            self.assertFalse((Path(d) / "out").exists())

    def test_enshwick_notice_wiring(self):
        self.assertIn("--enshwick-src", (HERE / "release.sh").read_text())
        self.assertIn("def enshwick_entries", (HERE / "lib" / "notices.py").read_text())


class EnshwickPinTests(unittest.TestCase):
    SCRIPT = ROOT / "native/plugins/enshwick/tools/build-runtime.sh"

    def test_pin_is_a_full_commit(self):
        s = self.SCRIPT.read_text()
        # any release tag (the pin moves with Enshwick's releases; alpha.14 since dev.23), always with its full commit
        self.assertRegex(s, r'\nTAG="v\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?"\n')
        self.assertRegex(s, r'\nCOMMIT="[0-9a-f]{40}"\n')

    def run_script(self, kiln_out, *args):
        env = {**os.environ, "KILN_OUT": str(kiln_out), "CARGO_TARGET_DIR": str(kiln_out / "cargo")}
        return subprocess.run(["bash", str(self.SCRIPT), *args], env=env, capture_output=True, text=True, timeout=60)

    def test_wrong_checkout_is_refused(self):
        with tempfile.TemporaryDirectory(prefix="kiln-enshwick-pin-") as d:
            out = Path(d)
            tag = re.search(r'\nTAG="([^"]+)"', self.SCRIPT.read_text()).group(1)
            src = out / "runtime-src" / f"enshwick-{tag}"
            src.mkdir(parents=True)
            git = ["git", "-C", str(src), "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false"]
            subprocess.run(["git", "init", "-q", str(src)], check=True)
            (src / "LICENSE").write_text("not enshwick\n")
            subprocess.run(git + ["add", "LICENSE"], check=True)
            subprocess.run(git + ["commit", "-q", "-m", "x"], check=True)
            r = self.run_script(out)
            self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
            self.assertIn("not the pinned", r.stderr)
            self.assertFalse((out / "enshwick-runtime").exists())

    def test_tag_without_commit_is_refused(self):
        with tempfile.TemporaryDirectory(prefix="kiln-enshwick-pin-") as d:
            r = self.run_script(Path(d), "--tag", "v9.9.9")
            self.assertEqual(r.returncode, 1)
            self.assertIn("go together", r.stderr)


class EnshwickBuildTests(unittest.TestCase):
    """Real Git repositories, local transport only; Cargo records what it would compile."""
    SCRIPT = EnshwickPinTests.SCRIPT
    REPO = "https://github.com/enshwick/enshwick.git"
    TAG = "v-test"

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="kiln-enshwick-build-")
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)
        self.out = self.dir / "out with spaces"
        self.src = self.out / "runtime-src" / ("enshwick-" + self.TAG)
        self.upstream = self.dir / "upstream"
        self.upstream.mkdir()
        self.git_bin = shutil.which("git")
        self.git(self.upstream, "init", "-q")
        for name, body in {"LICENSE": "upstream copyright\n", "Cargo.lock": "# pinned lock\n", "Cargo.toml": "[workspace]\n"}.items():
            (self.upstream / name).write_text(body)
        self.commit = self.commit_upstream()
        self.git(self.upstream, "tag", self.TAG)
        self.src.parent.mkdir(parents=True)
        subprocess.run([self.git_bin, "clone", "-q", str(self.upstream), str(self.src)], check=True)
        self.git(self.src, "remote", "set-url", "origin", self.REPO)
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        # Rewrite transport only, so get-url and config inspection still see the real upstream URL.
        self.executable("git", '''#!/usr/bin/env python3
import os, sys
args = sys.argv[1:]
if "status" in args and os.environ.get("TEST_STATUS_FAIL"):
    sys.exit(128)
if "clone" in args or "fetch" in args:
    args = ["-c", "url." + os.environ["TEST_UPSTREAM"] + ".insteadOf=" + os.environ["TEST_REPO"]] + args
os.execv(os.environ["TEST_GIT"], [os.environ["TEST_GIT"], *args])
''')
        self.executable("cargo", '''#!/usr/bin/env python3
import json, os, pathlib, sys
root = pathlib.Path.cwd()
if os.environ.get("TEST_SWAP"):
    src = pathlib.Path(os.environ["TEST_SOURCE"])
    src.rename(src.with_name(src.name + "-old"))
    src.mkdir()
    (src / "LICENSE").write_text("swapped copyright")
record = {"args": sys.argv[1:], "flags": os.environ.get("CARGO_ENCODED_RUSTFLAGS", "").split("\\x1f"),
          "license": (root / "LICENSE").read_text(), "lock": (root / "Cargo.lock").read_text(), "cwd": str(root)}
pathlib.Path(os.environ["TEST_RECORD"]).write_text(json.dumps(record))
dest = pathlib.Path(os.environ["CARGO_TARGET_DIR"]) / "x86_64-pc-windows-msvc/release"
dest.mkdir(parents=True, exist_ok=True)
(dest / "enshwick_loader.dll").write_bytes(b"MZ fixture")
''')
        self.record = self.dir / "cargo.json"
        self.env = {**os.environ, "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
                    "TEST_GIT": self.git_bin, "TEST_UPSTREAM": str(self.upstream), "TEST_REPO": self.REPO,
                    "TEST_RECORD": str(self.record), "TEST_SOURCE": str(self.src),
                    "KILN_OUT": str(self.out), "CARGO_TARGET_DIR": str(self.out / "cargo")}
        self.env.pop("CARGO_ENCODED_RUSTFLAGS", None)
        self.env.pop("RUSTFLAGS", None)

    def git(self, repo, *args):
        return subprocess.check_output([self.git_bin, "-C", str(repo), "-c", "user.name=t", "-c", "user.email=t@t",
                                        "-c", "commit.gpgsign=false", *args], text=True).strip()

    def commit_upstream(self):
        self.git(self.upstream, "add", ".")
        self.git(self.upstream, "commit", "-qm", "fixture")
        return self.git(self.upstream, "rev-parse", "HEAD")

    def executable(self, name, body):
        path = self.bin / name
        path.write_text(body)
        path.chmod(0o755)

    def run_build(self, **env):
        return subprocess.run(["bash", str(self.SCRIPT), "--tag", self.TAG, "--commit", self.commit],
                              env={**self.env, **env}, capture_output=True, text=True, timeout=30)

    def refused(self, **env):
        r = self.run_build(**env)
        self.assertNotEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertFalse(self.record.exists(), r.stdout + r.stderr)
        return r

    def test_changed_remote_is_refused(self):
        self.git(self.src, "remote", "set-url", "origin", "https://example.invalid/swapped.git")
        self.assertIn("remote", self.refused().stderr)

    def test_git_status_failure_is_not_treated_as_a_clean_checkout(self):
        self.refused(TEST_STATUS_FAIL="1")

    def test_fresh_clone_uses_the_tag_and_pin(self):
        shutil.rmtree(self.src)
        r = self.run_build()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual((self.out / "enshwick-runtime/enshwick-version.txt").read_text(), self.TAG + "\n" + self.commit + "\n")

    def test_reused_checkout_refetches_tag_and_refuses_a_moved_tag(self):
        (self.upstream / "LICENSE").write_text("changed upstream\n")
        self.commit_upstream()
        self.git(self.upstream, "tag", "-f", self.TAG)
        self.assertIn("pinned", self.refused().stderr)

    def test_dirty_ignored_and_index_hidden_changes_are_refused(self):
        for kind in ("tracked", "untracked", "ignored", "assume-unchanged", "skip-worktree"):
            with self.subTest(kind=kind):
                if kind in ("tracked", "assume-unchanged", "skip-worktree"):
                    if kind != "tracked":
                        self.git(self.src, "update-index", "--" + kind, "LICENSE")
                    (self.src / "LICENSE").write_text("tampered\n")
                else:
                    (self.src / "injected").write_text("tampered\n")
                    if kind == "ignored":
                        (self.src / ".git/info/exclude").write_text("injected\n")
                self.refused()
                self.git(self.src, "update-index", "--no-assume-unchanged", "--no-skip-worktree", "LICENSE")
                self.git(self.src, "reset", "--hard", "HEAD")
                self.git(self.src, "clean", "-fdx")

    def test_checkout_symlink_is_refused(self):
        moved = self.src.with_name("other")
        self.src.rename(moved)
        self.src.symlink_to(moved, target_is_directory=True)
        self.refused()

    def test_cached_git_objects_cannot_impersonate_the_pinned_bytes(self):
        oid = self.git(self.src, "rev-parse", "HEAD:LICENSE")
        data = b"tampered copyright\n"
        obj = self.src / ".git/objects" / oid[:2] / oid[2:]
        replacement = obj.with_suffix(".tmp")
        replacement.write_bytes(zlib.compress(b"blob " + str(len(data)).encode() + b"\0" + data))
        replacement.replace(obj)  # break the fixture clone's hardlink without altering the upstream
        (self.src / "LICENSE").write_bytes(data)
        self.git(self.src, "update-index", "--assume-unchanged", "LICENSE")
        self.refused()

    def test_pinned_submodule_and_lfs_are_refused(self):
        for kind in ("submodule", "lfs"):
            with self.subTest(kind=kind):
                if kind == "submodule":
                    self.git(self.upstream, "update-index", "--add", "--cacheinfo", "160000," + self.commit + ",nested")
                    self.git(self.upstream, "commit", "-qm", "gitlink")
                else:
                    self.git(self.upstream, "rm", "--cached", "nested")
                    (self.upstream / ".gitattributes").write_text("*.dat filter=lfs diff=lfs merge=lfs -text\n")
                    (self.upstream / "large.dat").write_text("version https://git-lfs.github.com/spec/v1\noid sha256:" + "a" * 64 + "\nsize 99\n")
                    self.commit_upstream()
                self.commit = self.git(self.upstream, "rev-parse", "HEAD")
                self.git(self.upstream, "tag", "-f", self.TAG)
                self.git(self.src, "fetch", str(self.upstream), "HEAD")
                self.git(self.src, "reset", "--hard", "FETCH_HEAD")
                self.refused()

    def test_locked_build_preserves_release_and_plain_rustflags(self):
        for flags in ({"CARGO_ENCODED_RUSTFLAGS": "--remap-path-prefix=/home/person=/build\x1f-Cdebuginfo=0"},
                      {"RUSTFLAGS": "--remap-path-prefix=/home/person=/build -Cdebuginfo=0"}):
            with self.subTest(flags=flags):
                r = self.run_build(**flags)
                self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
                record = json.loads(self.record.read_text())
                self.assertIn("--locked", record["args"])
                self.assertEqual(record["lock"], "# pinned lock\n")
                self.assertNotEqual(record["cwd"], str(self.src))
                self.assertIn("--remap-path-prefix=" + record["cwd"] + "=/enshwick", record["flags"])
                for flag in ("--remap-path-prefix=/home/person=/build", "-Cdebuginfo=0", "-Ctarget-feature=+crt-static", "-Ctarget-cpu=x86-64-v2"):
                    self.assertIn(flag, record["flags"])

    def test_swapping_cached_path_cannot_change_the_built_source_or_license(self):
        r = self.run_build(TEST_SWAP="1")
        if r.returncode == 0:
            self.assertEqual((self.out / "enshwick-runtime/LICENSE").read_text(), "upstream copyright\n")
        else:
            self.assertFalse((self.out / "enshwick-runtime/enshwick.dll").exists())


class EnshwickNoticeTests(unittest.TestCase):
    def test_reachable_workspace_and_external_crates_keep_their_actual_notices(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            (root / ".git").mkdir()
            (root / "LICENSE").write_text("Copyright upstream\nMIT permission\n")
            packages = []
            for name in ("enshwick-loader", "enshwick-api", "dependency", "build-only"):
                directory = root / name
                directory.mkdir()
                (directory / "Cargo.toml").touch()
                if name in ("dependency", "build-only"):
                    (directory / "LICENSE").write_text("Copyright " + name)
                packages.append({"id": name, "name": name, "version": "1", "license": "MIT",
                                 "manifest_path": str(directory / "Cargo.toml")})
            meta = {"packages": packages, "workspace_members": ["enshwick-loader", "enshwick-api"],
                    "resolve": {"nodes": [
                        {"id": "enshwick-loader", "deps": [{"pkg": "enshwick-api", "dep_kinds": [{"kind": None}]},
                                                            {"pkg": "build-only", "dep_kinds": [{"kind": "build"}]}]},
                        {"id": "enshwick-api", "deps": [{"pkg": "dependency", "dep_kinds": [{"kind": None}]}]},
                        {"id": "dependency", "deps": []}, {"id": "build-only", "deps": []}]}}
            with patch.object(notices.subprocess, "check_output", side_effect=["a" * 40, json.dumps(meta).encode()]):
                entries = notices.enshwick_entries(d)
            by_name = {e[0]: e for e in entries}
            self.assertIn("enshwick-api", by_name)
            self.assertNotIn("build-only", by_name)
            self.assertIn(("LICENSE", "Copyright dependency"), by_name["dependency"][4])
            self.assertIn(("LICENSE", "Copyright upstream\nMIT permission"), by_name["enshwick-api"][4])
            (root / "dependency/LICENSE").unlink()
            # no licence file but a declared licence with a standard text (bcdec_rs 0.2.1, MIT): no files, so the
            # renderer prints the standard text and says the package had none
            with patch.object(notices.subprocess, "check_output", side_effect=["a" * 40, json.dumps(meta).encode()]):
                entries = notices.enshwick_entries(d)
            self.assertEqual({e[0]: e for e in entries}["dependency"][4], [])
            # neither a file nor a licence with a known text: refused
            for p in packages:
                if p["name"] == "dependency":
                    p["license"] = "LicenseRef-Unknown"
            with patch.object(notices.subprocess, "check_output", side_effect=["a" * 40, json.dumps(meta).encode()]):
                with self.assertRaisesRegex(RuntimeError, "dependency.*licence"):
                    notices.enshwick_entries(d)


if __name__ == "__main__":
    unittest.main()
