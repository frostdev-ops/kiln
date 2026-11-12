#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""release.sh's Rust pin: the function itself, run with fake rustup and rustc programs. Nothing is built or installed."""
import os
import re
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
PINNED = re.search(r"^ARG RUST_VERSION=(.*)$", (HERE / "linux-build" / "Dockerfile").read_text(), re.M).group(1)
TARGET = "x86_64-pc-windows-msvc"

# rustup: knows the toolchains in $FAKE_TOOLCHAINS (space separated) and, for each, the targets in $FAKE_TARGETS.
RUSTUP = """#!/usr/bin/env bash
case "$1" in
  run) for t in $FAKE_TOOLCHAINS; do [[ $t == "$2" ]] && { echo "rustc $2 (fake)"; exit 0; }; done; echo "error: toolchain '$2' is not installed" >&2; exit 1;;
  target) for t in $FAKE_TARGETS; do echo "$t"; done;;
esac
"""
# rustc without rustup: its version, and where it keeps a target's library ($FAKE_LIBDIR; unset: it knows no such target)
RUSTC = """#!/usr/bin/env bash
if [[ $1 == --print ]]; then
  [[ -n ${FAKE_LIBDIR:-} ]] || { echo "error: could not find specification for target" >&2; exit 1; }
  echo "$FAKE_LIBDIR"; exit 0
fi
echo "rustc $FAKE_RUSTC (0000000 2026-01-01)"
"""


class RustPin(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        # a PATH with only what the function needs, so "no rustup on this machine" can be shown
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        for tool in ("bash", "sed", "grep", "head", "env"):
            (self.bin / tool).symlink_to(shutil.which(tool))
        fn = re.search(r"^pin_rust\(\) \{\n.*?^\}", (HERE / "release.sh").read_text(), re.M | re.S)
        self.assertIsNotNone(fn, "release.sh has no pin_rust()")
        self.harness = self.dir / "pin.sh"
        self.harness.write_text('set -euo pipefail\nhere=$1\ndie() { echo "release: $*" >&2; exit 1; }\nnote() { echo "$*"; }\n'
                                + fn.group() + '\npin_rust "$2"\necho "RUSTUP_TOOLCHAIN=${RUSTUP_TOOLCHAIN:-unset}"\n')

    def tearDown(self):
        self.tmp.cleanup()

    def program(self, name, text):
        p = self.bin / name
        p.write_text(text)
        p.chmod(p.stat().st_mode | stat.S_IEXEC)

    def run_pin(self, **env):
        full = {"PATH": str(self.bin), "HOME": str(self.dir), **env}
        return subprocess.run([str(self.bin / "bash"), str(self.harness), str(HERE), TARGET], env=full, capture_output=True, text=True)

    def test_the_dockerfile_names_one_version(self):
        self.assertRegex(PINNED, r"^\d+\.\d+\.\d+$")

    def test_release_calls_it_before_anything_is_built(self):
        text = (HERE / "release.sh").read_text()
        call = text.index("\npin_rust " + TARGET)
        self.assertLess(call, text.index("cargo build --release"))
        self.assertLess(call, text.index("cargo xwin build"))
        self.assertLess(call, text.index("linux-build/image.sh"))

    def test_the_images_version_becomes_the_hosts_toolchain(self):
        self.program("rustup", RUSTUP)
        r = self.run_pin(FAKE_TOOLCHAINS=f"stable {PINNED}", FAKE_TARGETS=f"x86_64-unknown-linux-gnu {TARGET}")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn(f"RUSTUP_TOOLCHAIN={PINNED}", r.stdout)
        self.assertIn(f"Rust {PINNED}", r.stdout)

    def test_a_missing_toolchain_stops_with_the_command_that_installs_it(self):
        self.program("rustup", RUSTUP)
        r = self.run_pin(FAKE_TOOLCHAINS="stable 1.0.0", FAKE_TARGETS=TARGET)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn(f"rustup toolchain install {PINNED} --profile minimal --target {TARGET}", r.stderr)
        self.assertNotIn("RUSTUP_TOOLCHAIN=", r.stdout)

    def test_a_toolchain_without_the_windows_target_stops_too(self):
        self.program("rustup", RUSTUP)
        r = self.run_pin(FAKE_TOOLCHAINS=PINNED, FAKE_TARGETS="x86_64-unknown-linux-gnu")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn(f"rustup target add --toolchain {PINNED} {TARGET}", r.stderr)

    def test_a_target_name_must_match_whole(self):
        self.program("rustup", RUSTUP)
        r = self.run_pin(FAKE_TOOLCHAINS=PINNED, FAKE_TARGETS=TARGET + "-extra")
        self.assertNotEqual(r.returncode, 0)

    def test_another_version_for_a_trial_build(self):
        self.program("rustup", RUSTUP)
        r = self.run_pin(FAKE_TOOLCHAINS=f"{PINNED} 1.2.3", FAKE_TARGETS=TARGET, KILN_RELEASE_RUST="1.2.3")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("RUSTUP_TOOLCHAIN=1.2.3", r.stdout)

    def test_a_channel_name_is_not_a_pin(self):
        self.program("rustup", RUSTUP)
        for moving in ("stable", "nightly", "1.99", "1.99.1-beta"):
            with self.subTest(moving):
                r = self.run_pin(FAKE_TOOLCHAINS="stable nightly 1.99 1.99.1-beta", FAKE_TARGETS=TARGET, KILN_RELEASE_RUST=moving)
                self.assertNotEqual(r.returncode, 0)
                self.assertIn("no Rust version to build with", r.stderr)

    def test_an_empty_override_means_the_images_version(self):
        self.program("rustup", RUSTUP)
        r = self.run_pin(FAKE_TOOLCHAINS=PINNED, FAKE_TARGETS=TARGET, KILN_RELEASE_RUST="")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn(f"RUSTUP_TOOLCHAIN={PINNED}", r.stdout)

    def libdir(self, *files):
        d = self.dir / "lib" / "rustlib" / TARGET / "lib"
        d.mkdir(parents=True, exist_ok=True)
        for f in files:
            (d / f).write_text("")
        return str(d)

    def test_without_rustup_the_distributions_rustc_must_be_that_version(self):
        self.program("rustc", RUSTC)
        lib = self.libdir("libstd-0123456789abcdef.rlib", "libcore-0123456789abcdef.rlib")
        ok = self.run_pin(FAKE_RUSTC=PINNED, FAKE_LIBDIR=lib)
        self.assertEqual(ok.returncode, 0, ok.stderr)
        self.assertIn("RUSTUP_TOOLCHAIN=unset", ok.stdout)
        other = self.run_pin(FAKE_RUSTC="1.0.0", FAKE_LIBDIR=lib)
        self.assertNotEqual(other.returncode, 0)
        self.assertIn(f"the release builds with Rust {PINNED}", other.stderr)
        longer = self.run_pin(FAKE_RUSTC=PINNED + "9", FAKE_LIBDIR=lib)  # 1.99.19 is not 1.99.1
        self.assertNotEqual(longer.returncode, 0)

    def test_without_rustup_the_windows_target_must_be_installed_too(self):
        # the right version alone is not enough: the Windows files need that target's standard library
        self.program("rustc", RUSTC)
        unknown = self.run_pin(FAKE_RUSTC=PINNED)  # rustc does not know the target at all
        self.assertNotEqual(unknown.returncode, 0)
        self.assertIn(f"has no {TARGET} library", unknown.stderr)
        self.assertIn(f"rustup toolchain install {PINNED} --profile minimal --target {TARGET}", unknown.stderr)
        missing = self.run_pin(FAKE_RUSTC=PINNED, FAKE_LIBDIR=str(self.dir / "not" / "there"))
        self.assertNotEqual(missing.returncode, 0)
        self.assertIn(str(self.dir / "not" / "there"), missing.stderr)
        empty = self.run_pin(FAKE_RUSTC=PINNED, FAKE_LIBDIR=self.libdir())  # the folder is there, the library is not
        self.assertNotEqual(empty.returncode, 0)
        self.assertIn(f"has no {TARGET} library", empty.stderr)
        self.assertNotIn("RUSTUP_TOOLCHAIN", empty.stdout)  # it stopped before anything went on
        there = self.run_pin(FAKE_RUSTC=PINNED, FAKE_LIBDIR=self.libdir("libstd-0123456789abcdef.rlib"))
        self.assertEqual(there.returncode, 0, there.stderr)

    def test_no_rust_at_all_stops(self):
        r = self.run_pin()
        self.assertNotEqual(r.returncode, 0)
        self.assertIn(f"the release builds with Rust {PINNED}", r.stderr)


if __name__ == "__main__":
    unittest.main()
