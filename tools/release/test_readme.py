#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Exercise only release.sh's README/listing blocks, never its builds, signing or publishing."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

HERE = Path(__file__).resolve().parent


class ReleaseListings(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="release-listings-")
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        for rel in ("sdk/Kiln.Api.0.2.0.nupkg", "sdk/api/index.html", "sdk/api/nested/type.html",
                    "sdk/feed/index.json", "sdk/feed/flatcontainer/kiln.api/index.json",
                    "nexus/README.txt", "runtime/kiln-runtime.zip"):
            p = self.root / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text("fixture", encoding="utf-8")
        self.script = (HERE / "release.sh").read_text(encoding="utf-8")

    def test_readme_lists_downloaded_files_not_unpublished_site_folders(self):
        (self.root / "README.txt.tmp").write_text("Files\n@FILES@\n", encoding="utf-8")
        block = self.script.split('python3 - "$out" <<\'PY\'\n', 1)[1].split("\nPY\n", 1)[0]
        result = subprocess.run(["python3", "-I", "-c", block, str(self.root)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        text = (self.root / "README.txt").read_text(encoding="utf-8")
        self.assertIn("sdk/Kiln.Api.0.2.0.nupkg", text)
        self.assertIn("runtime/kiln-runtime.zip", text)
        self.assertIn("README.txt", text)
        self.assertIn("SHA256SUMS", text)
        for unpublished in ("sdk/api", "sdk/feed", "nexus/"):
            self.assertNotIn(unpublished, text)
        self.assertFalse((self.root / "README.txt.tmp").exists())

    def test_terminal_listing_still_summarizes_both_site_folders(self):
        block = self.script.split('(cd "$out" && find . -type f', 1)[1].split('\ncat <<EOF', 1)[0]
        block = '(cd "$out" && find . -type f' + block
        result = subprocess.run(["bash", "-euo", "pipefail", "-c", 'out=$1; log=$2\n' + block, "listing",
                                 str(self.root), os.devnull], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("sdk/api/ (2 files)", result.stdout)
        self.assertIn("sdk/feed/ (2 files)", result.stdout)
        self.assertNotIn("sdk/api/index.html", result.stdout)
        self.assertNotIn("sdk/feed/index.json", result.stdout)


if __name__ == "__main__":
    unittest.main()
