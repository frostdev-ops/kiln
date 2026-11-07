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
import subprocess
import tempfile
import unittest
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
spec = importlib.util.spec_from_file_location("plugins", HERE / "lib" / "plugins.py")
pl = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pl)

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

    def test_enshwick_notice_wiring(self):
        self.assertIn("--enshwick-src", (HERE / "release.sh").read_text())
        self.assertIn("def enshwick_entries", (HERE / "lib" / "notices.py").read_text())


class EnshwickPinTests(unittest.TestCase):
    SCRIPT = ROOT / "native/plugins/enshwick/tools/build-runtime.sh"

    def test_pin_is_a_full_commit(self):
        s = self.SCRIPT.read_text()
        self.assertRegex(s, r'\nTAG="v1\.0\.0-alpha\.13"\n')
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


if __name__ == "__main__":
    unittest.main()
