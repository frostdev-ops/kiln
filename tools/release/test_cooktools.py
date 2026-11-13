#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The Python the Windows launcher ships (tools/package/fetch-python.sh): the pin, the hash check, what the unpacked
package must hold. Made-up zips from a file:// source; nothing is downloaded and the real cache is never touched."""
import hashlib
import os
import re
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parent / "package" / "fetch-python.sh"
WHOLE = ["python.exe", "python314.zip", "python314._pth", "_zstd.pyd", "LICENSE.txt", "python314.dll"]


class FetchPython(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.cache = self.dir / "cache"

    def tearDown(self):
        self.tmp.cleanup()

    def package(self, names, name="python.zip"):
        z = self.dir / name
        with zipfile.ZipFile(z, "w") as f:
            for n in names:
                f.writestr(n, "made up: " + n)
        return z, hashlib.sha256(z.read_bytes()).hexdigest()

    def run_script(self, zip_path, sha, *args, release=False):
        env = dict(os.environ, KILN_FETCH_PYTHON_TEST="1", KILN_FETCH_PYTHON_URL=zip_path.as_uri(), KILN_FETCH_PYTHON_SHA256=sha,
                   KILN_FETCH_PYTHON_CACHE=str(self.cache))
        env.pop("KILN_RELEASE_BUILD", None)
        if release:
            env["KILN_RELEASE_BUILD"] = "1"
        return subprocess.run(["bash", str(SCRIPT), *args], env=env, capture_output=True, text=True)

    def test_the_pin_is_one_exact_file_of_python_org(self):
        text = SCRIPT.read_text()
        version = re.search(r"^VERSION=(\S+)$", text, re.M).group(1)
        self.assertRegex(version, r"^3\.(1[4-9]|[2-9]\d)\.\d+$", "3.14 or newer: its standard library reads zstd")
        self.assertIn("URL=https://www.python.org/ftp/python/$VERSION/python-$VERSION-embed-amd64.zip", text)
        self.assertRegex(re.search(r"^SHA256=(\S+)$", text, re.M).group(1), r"^[0-9a-f]{64}$")

    def test_a_package_with_the_pinned_hash_is_unpacked_once(self):
        z, sha = self.package(WHOLE)
        r = self.run_script(z, sha, "--print-root")
        self.assertEqual(r.returncode, 0, r.stderr)
        root = Path(r.stdout.strip())
        self.assertEqual(root, self.cache / "root")
        self.assertEqual(sorted(p.name for p in root.iterdir()), sorted(WHOLE), "nothing of ours is added to what ships")
        stamp = (root / "python.exe").stat().st_mtime_ns
        # cached: verified, not fetched or unpacked again (the source is gone by now)
        z.unlink()
        r = self.run_script(z, sha, "--check", "--print-root")
        self.assertEqual((r.returncode, r.stdout.strip()), (0, str(root)), r.stderr)
        r = self.run_script(z, sha)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual((root / "python.exe").stat().st_mtime_ns, stamp)

    def test_a_wrong_hash_keeps_nothing(self):
        z, _ = self.package(WHOLE)
        r = self.run_script(z, "0" * 64)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("sha256 mismatch", r.stderr)
        self.assertFalse((self.cache / "python.zip").exists())
        self.assertFalse((self.cache / "root").exists())
        self.assertEqual(list(self.cache.glob("*.download")), [])

    def test_check_never_fetches(self):
        z, sha = self.package(WHOLE)
        r = self.run_script(z, sha, "--check")
        self.assertEqual(r.returncode, 1)
        self.assertIn("missing or not the pinned", r.stderr)
        self.assertFalse(self.cache.exists())

    def test_a_package_without_zstd_is_refused(self):
        z, sha = self.package([n for n in WHOLE if n != "_zstd.pyd"])
        r = self.run_script(z, sha)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("_zstd.pyd", r.stderr)
        self.assertFalse((self.cache / "root").exists())

    def test_a_changed_pin_unpacks_again_and_a_stale_root_fails_the_check(self):
        z, sha = self.package(WHOLE)
        self.assertEqual(self.run_script(z, sha).returncode, 0)
        z2, sha2 = self.package(WHOLE + ["new-in-this-version.pyd"], name="python2.zip")
        r = self.run_script(z2, sha2, "--check")
        self.assertEqual(r.returncode, 1, "the cached zip is the old pin's")
        r = self.run_script(z2, sha2)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertTrue((self.cache / "root" / "new-in-this-version.pyd").is_file())
        # the folder tampered with after the unpack: the check sees a missing file
        (self.cache / "root" / "python.exe").unlink()
        r = self.run_script(z2, sha2, "--check")
        self.assertEqual(r.returncode, 1)
        self.assertIn("python.exe", r.stderr)

    def test_the_test_overrides_are_refused_in_a_release_build(self):
        z, sha = self.package(WHOLE)
        r = self.run_script(z, sha, release=True)
        self.assertEqual(r.returncode, 2)
        self.assertIn("never a release build", r.stderr)
        self.assertFalse(self.cache.exists())


if __name__ == "__main__":
    unittest.main()
