#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Offline regressions for the Linux release audit. No daemon, network, signing keys, or release builds.
  python3 -I tools/release/test_linux_release.py
KILN_RELEASE_TEST_ROOT can point at an immutable export to reproduce failures before the fix.
"""
import contextlib
import gzip
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import signal
import struct
import subprocess
import tarfile
import tempfile
import time
import unittest
from unittest.mock import patch
import zipfile

HERE = Path(os.environ.get("KILN_RELEASE_TEST_ROOT", Path(__file__).resolve().parent))


def module(name):
    spec = importlib.util.spec_from_file_location(name, HERE / "lib" / f"{name}.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


release = module("release")
notices = module("notices")


def rpm_fixture(data, compressor="gzip"):
    def member(name, data, ino):
        name = name.encode() + b"\0"
        fields = [ino, 0o100755, 0, 0, 1, 0, len(data), 0, 0, 0, 0, len(name), 0]
        h = b"070701" + b"".join(f"{n:08x}".encode() for n in fields) + name
        return h + b"\0" * (-len(h) % 4) + data + b"\0" * (-len(data) % 4)

    def header(entries, store):
        return (b"\x8e\xad\xe8\x01" + b"\0" * 4 + struct.pack(">II", len(entries), len(store))
                + b"".join(struct.pack(">IIII", *e) for e in entries) + store)

    body = member("usr/bin/kiln-launcher", data, 1) + member("TRAILER!!!", b"", 0)
    lead = struct.pack(">4sBBHH66sHH16s", b"\xed\xab\xee\xdb", 3, 0, 0, 1, b"probe", 1, 5, b"")
    store, entries = b"", []
    for tag, v in [(1000, b"probe\0"), (1124, b"cpio\0"), (1125, compressor.encode() + b"\0")]:
        entries.append((tag, 6, len(store), 1))
        store += v
    if compressor == "gzip":
        payload = gzip.compress(body, mtime=0)
    else:
        import compression.zstd
        payload = compression.zstd.compress(body)
    return lead + header([], b"") + header(entries, store) + payload


class LinuxReleaseTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="kiln-release-test-")
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)

    def scan(self, path, limit="2.35"):
        s = release.GlibcScan(limit, str(self.dir))
        with patch.object(release, "elf_glibc", return_value=({"2.99"}, None)):
            s.scan_top(str(path), path.name)
        return s

    def elf(self, name="elf"):
        p = self.dir / name
        p.write_bytes(b"\x7fELF" + b"\0" * 508)
        return p

    def test_only_glibc_requirements_not_definitions_or_filename(self):
        out = (b"/tmp/GLIBC_99.0: file format elf64-x86-64\nVersion definitions:\n"
               b"1 0x00 GLIBC_88.0\nDYNAMIC SYMBOL TABLE:\n"
               b"000 g DF .text 000 GLIBC_77.0 definition\nVersion References:\n"
               b"  required from libc.so.6:\n    0x123 0x00 02 GLIBC_2.35\n"
               b"    0x456 0x00 03 GLIBC_ABI_DT_RELR\n")
        with patch.object(release.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, out, b"")):
            versions, error = release.elf_glibc("/tmp/GLIBC_99.0")
        self.assertIsNone(error)
        self.assertEqual(versions, {"2.35", "ABI_DT_RELR"})

    def test_objdump_error_is_not_a_static_success(self):
        with patch.object(release.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, b"", b"no symbols; damaged object")):
            _, error = release.elf_glibc("elf")
        self.assertIsNotNone(error)

    def test_nested_extensionless_archives_and_tar_are_checked(self):
        data = self.elf().read_bytes()
        for _ in range(6):
            b = io.BytesIO()
            with zipfile.ZipFile(b, "w") as z:
                z.writestr("no-extension", data)
            data = b.getvalue()
        p = self.dir / "nested.tar.gz"
        with tarfile.open(p, "w:gz") as t:
            info = tarfile.TarInfo("payload")
            info.size = len(data)
            t.addfile(info, io.BytesIO(data))
        s = self.scan(p)
        self.assertEqual(s.elfs, 1)
        self.assertTrue(any("GLIBC_2.99" in p for p in s.problems), s.problems)

    def test_recursion_limit_fails_closed(self):
        data = b"end"
        for _ in range(34):
            b = io.BytesIO()
            with zipfile.ZipFile(b, "w") as z:
                z.writestr("next.zip", data)
            data = b.getvalue()
        p = self.dir / "deep.zip"
        p.write_bytes(data)
        self.assertTrue(any("nesting" in p for p in self.scan(p).problems))

    def test_top_symlink_is_checked(self):
        p = self.dir / "alias"
        p.symlink_to(self.elf())
        self.assertEqual(self.scan(p).elfs, 1)

    def test_external_symlink_fails_closed(self):
        p = self.dir / "external"
        p.symlink_to("/bin/true")
        self.assertTrue(any("symlink" in p for p in self.scan(p).problems))

    def test_zip_symlink_is_not_flattened_into_text(self):
        p = self.dir / "links.zip"
        with zipfile.ZipFile(p, "w") as z:
            info = zipfile.ZipInfo("external")
            info.create_system = 3
            info.external_attr = 0o120777 << 16
            z.writestr(info, "/bin/true")
        self.assertTrue(any("symlink" in p for p in self.scan(p).problems))

    def test_rpm_compressed_payload_is_actually_checked(self):
        for compression in ("gzip", "zstd"):
            with self.subTest(compression=compression):
                p = self.dir / f"probe-{compression}.rpm"
                p.write_bytes(rpm_fixture(self.elf().read_bytes(), compression))
                s = self.scan(p)
                self.assertEqual(s.elfs, 1, s.problems)
                self.assertTrue(any("GLIBC_2.99" in p for p in s.problems), s.problems)

    def test_empty_linux_package_cannot_pass(self):
        p = self.elf("empty.AppImage")
        data = bytearray(p.read_bytes())
        data[8:11] = b"AI\x02"
        p.write_bytes(data)
        with patch.object(release.GlibcScan, "extract"):
            s = self.scan(p, "99.0")
        self.assertTrue(any("no ELF" in p for p in s.problems))

    def test_unknown_glibc_marker_is_a_failure(self):
        s = release.GlibcScan("2.35", str(self.dir))
        with patch.object(release, "elf_glibc", return_value=({"ABI_FUTURE"}, None)):
            s.scan_top(str(self.elf()), "elf")
        self.assertTrue(s.problems)

    def test_appimage_inspection_does_not_execute_it(self):
        p = self.elf("app.AppImage")
        with patch.object(release, "extract") as extract, patch.object(release.subprocess, "run") as run:
            release.GlibcScan.extract("appimage", str(p), str(self.dir))
        extract.assert_called_once()
        run.assert_not_called()

    def test_append_notices_preserves_host_sections(self):
        p = self.dir / "notices.txt"
        p.write_text("host NuGet licenses\n")
        with patch.object(notices, "appimage_libs", return_value=[("lib:amd64", "1", "", "", [("copyright", "license")])]), \
             patch.object(notices, "cargo_crates", side_effect=AssertionError("must not regenerate host notices")), \
             patch("sys.argv", ["notices.py", "--src", "/unused", "--out", str(p), "--append-appimage", "--appimage-root", "/unused"]), \
             contextlib.redirect_stdout(io.StringIO()):
            notices.main()
        self.assertTrue(p.read_text().startswith("host NuGet licenses\n"))
        self.assertIn("lib:amd64", p.read_text())

    def test_scan_home_path_context_redacts_secrets(self):
        secret = b"private-password-value"
        scanner = release.Scanner([secret], str(self.dir))
        scanner.scan_bytes("artifact", b"/home/player/ " + secret)
        self.assertTrue(scanner.problems)
        self.assertNotIn(secret.decode(), "\n".join(scanner.problems))

    def test_short_key_password_is_not_ignored(self):
        password = self.dir / "updater.key.password"
        password.write_bytes(b"short\n")
        self.assertIn(b"short", release.secrets_from_files([str(password)]))

    def test_directory_symlinks_are_checked_without_escaping_release(self):
        root = self.dir / "release"
        root.mkdir()
        target = root / "windows"
        target.mkdir()
        (target / "elf").write_bytes(self.elf().read_bytes())
        (root / "linux-link").symlink_to(target, target_is_directory=True)
        args = type("Args", (), {"dir": str(root), "files": [], "max": "2.35", "tmp": str(self.dir)})()
        with patch.object(release, "elf_glibc", return_value=({"2.99"}, None)), \
             contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                release.cmd_glibc(args)
        (root / "linux-link").unlink()
        (root / "external-dir").symlink_to(self.dir, target_is_directory=True)
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                release.cmd_glibc(args)

    def test_multiarch_package_identity_and_documentation_path(self):
        lib = self.dir / "libprobe.so.1"
        lib.write_bytes(b"lib")
        queries, docs = [], []
        def output(cmd, **kwargs):
            queries.append(cmd)
            if cmd[0] == "pacman":
                raise FileNotFoundError()
            if cmd[0] == "dpkg":
                return "libprobe:amd64: /usr/lib/libprobe.so.1\n"
            return "1.2\thttps://example.invalid"
        with patch.object(notices.glob, "glob", return_value=[str(lib)]), \
             patch.object(notices.os, "walk", return_value=[("/usr/lib", [], [lib.name])]), \
             patch.object(notices.subprocess, "check_output", side_effect=output), \
             patch.object(notices, "license_files", side_effect=lambda p: docs.append(p) or []):
            entries = notices.appimage_libs("/unused")
        self.assertEqual(entries[0][0], "libprobe:amd64")
        self.assertTrue(any(cmd[-1] == "libprobe:amd64" and cmd[0] == "dpkg-query" for cmd in queries))
        self.assertIn("/usr/share/doc/libprobe", docs)

    def mock_docker(self):
        bindir = self.dir / "bin"
        bindir.mkdir()
        docker = bindir / "docker"
        docker.write_text('''#!/usr/bin/python3
import json, os, sys, time
with open(os.environ["MOCK_CALLS"], "a") as f: f.write(json.dumps(sys.argv[1:])+"\\n")
if sys.argv[1:3] == ["image", "inspect"] and "--format" in sys.argv: print("sha256:" + "a"*64)
if sys.argv[1] == "run":
    if os.environ.get("MOCK_BLOCK"): time.sleep(30)
    sys.exit(int(os.environ.get("MOCK_EXIT", "0")))
''')
        docker.chmod(0o755)
        self.calls = self.dir / "calls.jsonl"
        return {**os.environ, "PATH": f"{bindir}:/usr/bin:/bin", "MOCK_CALLS": str(self.calls),
                "KILN_GLIBC_DIR": str(self.dir / "cache")}

    def script(self, name, *args, env=None):
        return subprocess.run(["bash", str(HERE / "linux-build" / name), *map(str, args)], env=env,
                              cwd=self.dir, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    def test_secret_mount_rejected_before_docker(self):
        env = self.mock_docker()
        home = self.dir / "home"
        home.mkdir()
        r = self.script("run.sh", "--mount", home, "--", "true", env={**env, "HOME": str(home)})
        self.assertNotEqual(r.returncode, 0)
        self.assertFalse(self.calls.exists(), r.stderr)

    def test_mount_spaces_symlinks_and_failure_cleanup(self):
        env = self.mock_docker()
        actual = self.dir / "actual folder"
        actual.mkdir()
        alias = self.dir / "alias folder"
        alias.symlink_to(actual, target_is_directory=True)
        r = self.script("run.sh", "--mount", alias, "--", "printf", "%s", "one argument", env={**env, "MOCK_EXIT": "17"})
        self.assertEqual(r.returncode, 17, r.stderr)
        calls = [json.loads(l) for l in self.calls.read_text().splitlines()]
        run = next(c for c in calls if c[0] == "run")
        self.assertIn(f"{actual}:{alias}:rw", run)
        self.assertEqual(run[-1], "one argument")
        self.assertTrue(any(c[:2] == ["rm", "-f"] for c in calls))

    def test_build_signal_removes_container(self):
        env = self.mock_docker()
        p = subprocess.Popen(["bash", str(HERE / "linux-build/run.sh"), "--mount", str(self.dir), "--", "true"],
                             cwd=self.dir, env={**env, "MOCK_BLOCK": "1"}, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            deadline = time.monotonic() + 5
            while time.monotonic() < deadline:
                if self.calls.exists() and any(json.loads(l)[0] == "run" for l in self.calls.read_text().splitlines()):
                    break
                time.sleep(.02)
            p.send_signal(signal.SIGTERM)
            p.communicate(timeout=5)
            calls = [json.loads(l) for l in self.calls.read_text().splitlines()]
            self.assertTrue(any(c[:2] == ["rm", "-f"] for c in calls))
            self.assertEqual(p.returncode, 143)
        finally:
            if p.poll() is None:
                p.kill()
                p.communicate()

    def test_start_refuses_output_inside_release_without_deleting(self):
        env = self.mock_docker()
        rel = self.dir / "release"
        (rel / "linux").mkdir(parents=True)
        output = rel / "results"
        output.mkdir()
        marker = output / "keep"
        marker.write_text("preserve")
        r = self.script("start-test.sh", rel, "--out", output, env=env)
        self.assertNotEqual(r.returncode, 0)
        self.assertEqual(marker.read_text(), "preserve")

    def test_start_propagates_docker_failure_and_cleans_up(self):
        env = self.mock_docker()
        rel = self.dir / "release"
        (rel / "linux").mkdir(parents=True)
        output = self.dir / "results"
        r = self.script("start-test.sh", rel, "--out", output, env={**env, "MOCK_EXIT": "23"})
        self.assertEqual(r.returncode, 23, r.stderr)
        self.assertEqual(output.stat().st_mode & 0o777, 0o700)
        calls = [json.loads(l) for l in self.calls.read_text().splitlines()]
        self.assertTrue(any(c[:2] == ["rm", "-f"] for c in calls))

    def test_start_does_not_ignore_a_failed_log_writer(self):
        env = self.mock_docker()
        tee = self.dir / "bin" / "tee"
        tee.write_text("#!/bin/sh\ncat >/dev/null\nexit 29\n")
        tee.chmod(0o755)
        rel = self.dir / "release"
        (rel / "linux").mkdir(parents=True)
        r = self.script("start-test.sh", rel, "--out", self.dir / "results", env=env)
        self.assertEqual(r.returncode, 29, r.stderr)

    def test_container_cli_failure_is_fatal_even_with_version_text(self):
        # BASH_ENV stubs setup before the first CLI, so this executes the real script without root/host changes.
        stubs = self.dir / "stubs.sh"
        stubs.write_text('''getent() { return 0; }
useradd() { :; }
install() { :; }
cd() { builtin cd "$MOCK_DIR"; }
runuser() { echo 'kilnctl 1.0'; return 19; }
''')
        r = self.script("start-test-container.sh", env={**os.environ, "BASH_ENV": str(stubs),
                        "MOCK_DIR": str(self.dir), "OWNER_UID": "1000", "OWNER_GID": "1000"})
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("kilnctl --version failed", r.stderr)
        self.assertNotIn("RESULT: OK", r.stdout)

    def test_nexus_start_selects_variant_and_keeps_release_read_only(self):
        env = self.mock_docker()
        rel = self.dir / "release"
        (rel / "linux").mkdir(parents=True)
        (rel / "nexus/linux").mkdir(parents=True)
        r = self.script("start-test.sh", rel, "--distribution", "nexus", "--out", self.dir / "results", env=env)
        self.assertEqual(r.returncode, 0, r.stderr)
        calls = [json.loads(l) for l in self.calls.read_text().splitlines()]
        run = next(c for c in calls if c[0] == "run")
        self.assertIn("DISTRIBUTION=nexus", run)
        self.assertIn(f"{rel}:/r:ro", run)

    def test_cli_nonzero_exit_cannot_be_hidden_by_expected_text(self):
        out = self.dir / "release"
        for d, name in [("runtime", "kilnctl"), ("sdk", "kiln")]:
            p = out / d / name
            p.parent.mkdir(parents=True)
            p.write_text(f"#!/bin/sh\necho '{name} 1.0'\nexit 19\n")
            p.chmod(0o755)
        r = subprocess.run(["bash", "-c", 'set -euo pipefail; source "$1"; out=$2; if check_cli_versions; then exit 0; else exit 1; fi',
                            "test", str(HERE / "checks.sh"), str(out)], capture_output=True)
        self.assertNotEqual(r.returncode, 0)


if __name__ == "__main__":
    unittest.main()
