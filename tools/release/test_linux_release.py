#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Offline regressions for the Linux release audit. No daemon, network, signing keys, or release builds.
  python3 -I tools/release/test_linux_release.py
KILN_RELEASE_TEST_ROOT can point at an immutable export to reproduce failures before the fix.
"""
import contextlib
import base64
import gzip
import importlib.util
import io
import json
import os
import re
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
PUBLIC_CLIENT_ID = "unit-test-registration"
PUBLIC_REDIRECT_URI = "http://127.0.0.1:8089/callback"


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

    def release_env(self, flag="1", client_id=PUBLIC_CLIENT_ID, redirect_uri=PUBLIC_REDIRECT_URI, base=None):
        env = dict(os.environ if base is None else base)
        for key, value in [("KILN_NEXUS_ENABLED", flag), ("KILN_NEXUS_OAUTH_CLIENT_ID", client_id),
                           ("KILN_NEXUS_OAUTH_REDIRECT_URI", redirect_uri)]:
            env.pop(key, None)
            if value is not None:
                env[key] = value
        return env

    def selftest_report(self, distribution="nexus", enabled=True):
        return {"ok": True, "passed": 1, "failed": 0, "distribution": distribution,
                "checks": [{"name": "fixture", "ok": True, "detail": "offline fixture"}],
                "buildMarker": release.BUILD_MARKERS[distribution].decode(), "releaseBuild": True,
                "nexusOAuth": {"enabled": enabled, "configured": enabled,
                               "clientId": PUBLIC_CLIENT_ID if enabled else None,
                               "redirectUri": PUBLIC_REDIRECT_URI if enabled else None}}

    def check_report(self, report, distribution="nexus", **expected):
        path = self.dir / "selftest.json"
        path.write_text(json.dumps(report))
        return subprocess.run(["bash", "-c", 'set -euo pipefail; source "$1"; ck_report "$2" "$3"',
                               "test", str(HERE / "checks.sh"), str(path), distribution],
                              env=self.release_env(**expected), capture_output=True, text=True)

    def updater_check(self, data, absent=True):
        pub = base64.b64encode(b"untrusted comment: generated test key\n" + base64.b64encode(b"Ed" + os.urandom(40)) + b"\n")
        needles = release.updater_needles(pubkey=pub)
        binary = self.dir / "kiln-launcher"
        binary.write_bytes(data)
        result = release.updater_scan(str(binary), binary.name, needles, str(self.dir))
        return release.updater_problems(result, binary.name, needles, absent)

    def test_updater_scan_requires_native_binary_and_positive_distribution(self):
        marker = release.BUILD_MARKERS["nexus"]
        self.assertTrue(self.updater_check(marker))
        self.assertTrue(self.updater_check(b"\x7fELF" + bytes(60)))
        self.assertFalse(self.updater_check(b"\x7fELF" + bytes(60) + marker))
        self.assertTrue(self.updater_check(b"\x7fELF" + bytes(60) + release.BUILD_MARKERS["direct"]))

    def test_updater_scan_checks_every_binary_and_deeply_nested_archives(self):
        pub = base64.b64encode(b"untrusted comment: generated test key\n" + base64.b64encode(b"Ed" + os.urandom(40)) + b"\n")
        needles = release.updater_needles(pubkey=pub)
        nested = io.BytesIO()
        with zipfile.ZipFile(nested, "w") as z:
            z.writestr("renamed.dll", b"MZ" + b"tauri-plugin-updater")
        data = nested.getvalue()
        for _ in range(6):
            nested = io.BytesIO()
            with zipfile.ZipFile(nested, "w") as z: z.writestr("next.zip", data)
            data = nested.getvalue()
        path = self.dir / "nested.zip"
        with zipfile.ZipFile(path, "w") as z:
            z.writestr("kiln-launcher", b"\x7fELF" + bytes(60) + release.BUILD_MARKERS["nexus"])
            z.writestr("nested.zip", data)
        result = release.updater_scan(str(path), path.name, needles, str(self.dir))
        problems = release.updater_problems(result, path.name, needles, True)
        self.assertTrue(any("updater plugin" in p for p in problems), problems)

    def test_updater_scan_depth_limit_and_external_symlink_fail_closed(self):
        needles = release.updater_needles(pubkey=b"generated-public-key")
        path = self.dir / "next.zip"
        with zipfile.ZipFile(path, "w") as z:
            z.writestr("kiln-launcher", b"\x7fELF" + release.BUILD_MARKERS["nexus"])
        result = release.updater_scan(str(path), path.name, needles, str(self.dir), depth=32)
        self.assertTrue(any("nesting" in p for p in result["problems"]))
        link = self.dir / "kiln-launcher"
        link.symlink_to("/bin/true")
        result = release.updater_scan(str(link), link.name, needles, str(self.dir))
        self.assertTrue(any("symlink" in p for p in result["problems"]))

    def test_updater_scan_catches_utf16_feeds_and_each_direct_binary_must_have_its_feeds(self):
        marker = b"\x7fELF" + bytes(60) + release.BUILD_MARKERS["nexus"]
        self.assertTrue(self.updater_check(marker + "frostdev.io/kiln/updates".encode("utf-16le")))
        self.assertTrue(self.updater_check(b"\x7fELF" + release.BUILD_MARKERS["direct"], absent=False))

    def test_nexus_upload_gate_rejects_a_direct_build_even_in_nexus_folder(self):
        spec = importlib.util.spec_from_file_location("nexus_gate", HERE / "nexus.py")
        nexus = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(nexus)
        repo = self.dir / "repo"
        cfg = repo / "launcher/src-tauri"
        cfg.mkdir(parents=True)
        (cfg / "tauri.conf.json").write_text(json.dumps({"plugins": {"updater": {"pubkey": base64.b64encode(os.urandom(42)).decode()}}}))
        scan_dir = repo / "tools/release/lib"
        scan_dir.mkdir(parents=True)
        shutil.copy(HERE / "lib/release.py", scan_dir)
        path = self.dir / "nexus.zip"
        for dist in ("nexus", "direct"):
            with zipfile.ZipFile(path, "w") as z:
                z.writestr("kiln-launcher", b"\x7fELF" + bytes(60) + release.BUILD_MARKERS[dist])
            if dist == "nexus":
                nexus.validate_launcher_archive(str(path), str(repo))
            else:
                with self.assertRaises(nexus.Fail):
                    nexus.validate_launcher_archive(str(path), str(repo))

    def test_selftest_report_requires_the_matching_compiled_marker(self):
        for marker, ok in [(release.BUILD_MARKERS["nexus"].decode(), True), (None, False), (release.BUILD_MARKERS["direct"].decode(), False)]:
            report = self.selftest_report()
            report["buildMarker"] = marker
            r = self.check_report(report)
            self.assertEqual(r.returncode == 0, ok, r.stderr)

    def test_official_selftest_report_refuses_missing_or_unconfigured_oauth(self):
        registered = self.selftest_report()["nexusOAuth"]
        for oauth, release_build, ok in [(None, True, False), ({"configured": False}, True, False),
                ({"configured": True}, True, False), (registered, False, False), (registered, True, True),
                ({"enabled": False, "configured": False, "clientId": None, "redirectUri": None}, True, True),
                ({"enabled": False, "configured": True, "clientId": "stale", "redirectUri": None}, True, False),
                ({"enabled": "0", "configured": False}, True, False),
                ({"enabled": True, "configured": False}, True, False)]:
            report = self.selftest_report()
            report.update(releaseBuild=release_build, nexusOAuth=oauth)
            r = self.check_report(report, flag="0" if oauth and oauth.get("enabled") is False else "1")
            self.assertEqual(r.returncode == 0, ok, r.stderr)

    def test_selftest_report_matches_expected_capability_in_each_distribution(self):
        for distribution in ("direct", "nexus"):
            for flag in (None, "1", "0"):
                for enabled in (False, True):
                    with self.subTest(distribution=distribution, flag=flag, enabled=enabled):
                        r = self.check_report(self.selftest_report(distribution, enabled), distribution, flag=flag)
                        self.assertEqual(r.returncode == 0, enabled == (flag != "0"), r.stdout + r.stderr)

    def test_selftest_report_matches_each_expected_public_registration(self):
        for distribution in ("direct", "nexus"):
            for flag in (None, "1"):
                for field, env_key, other in [("clientId", "client_id", "different-public-registration"),
                        ("redirectUri", "redirect_uri", "http://127.0.0.1:8090/other/callback")]:
                    with self.subTest(distribution=distribution, flag=flag, field=field):
                        report = self.selftest_report(distribution)
                        r = self.check_report(report, distribution, flag=flag, **{env_key: other})
                        self.assertNotEqual(r.returncode, 0, r.stdout + r.stderr)
                        report["nexusOAuth"][field] = other
                        r = self.check_report(report, distribution, flag=flag)
                        self.assertNotEqual(r.returncode, 0, r.stdout + r.stderr)
                        r = self.check_report(report, distribution, flag=flag, **{env_key: other})
                        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_selftest_report_rejects_malformed_expected_capability(self):
        for flag in ("", "false", "true", " 0", "0 ", "00", "01", "2", "\n1", "١"):
            for enabled in (False, True):
                with self.subTest(flag=flag, enabled=enabled):
                    r = self.check_report(self.selftest_report(enabled=enabled), flag=flag)
                    self.assertNotEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_selftest_report_requires_valid_expected_registration(self):
        invalid = [("clientId", "client_id", value) for value in
                   (None, "", "bad registration", "bad\nregistration", "é", "x" * 129)]
        invalid += [("redirectUri", "redirect_uri", value) for value in
                    (None, "", "https://example.invalid/callback", "http://localhost:8089/callback",
                     "http://127.0.0.1:08089/callback", "http://127.0.0.1:0/callback",
                     "http://127.0.0.1:65536/callback", "http://127.0.0.1:+8089/callback",
                     "http://127.0.0.1:8089/", "http://127.0.0.1:8089/a/../callback",
                     "http://127.0.0.1:8089/a/./callback", "http://127.0.0.1:8089/a//callback",
                     "http://127.0.0.1:8089/callback/", "http://127.0.0.1:8089/callback?code=fake",
                     "http://127.0.0.1:8089/callback#fragment", "http://127.0.0.1:8089/" + "x" * 129)]
        for flag in (None, "1"):
            for field, env_key, value in invalid:
                with self.subTest(flag=flag, field=field, value=value):
                    report = self.selftest_report()
                    r = self.check_report(report, flag=flag, **{env_key: value})
                    self.assertNotEqual(r.returncode, 0, r.stdout + r.stderr)
                    # Matching malformed values must still fail syntax validation.
                    report["nexusOAuth"][field] = value
                    r = self.check_report(report, flag=flag, **{env_key: value})
                    self.assertNotEqual(r.returncode, 0, r.stdout + r.stderr)
        for client_id, redirect_uri in [(None, None), ("", ""), ("ignored invalid id", "invalid")]:
            r = self.check_report(self.selftest_report(enabled=False), flag="0",
                                  client_id=client_id, redirect_uri=redirect_uri)
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_selftest_report_accepts_registration_shape_boundaries(self):
        for client_id, redirect_uri in [("x", "http://127.0.0.1:1/c"),
                ("A" * 128, "http://127.0.0.1:65535/" + "p" * 128),
                ("a_B.c-9", "http://127.0.0.1:8089/.well-known/oauth_v1/callback-2")]:
            with self.subTest(client_id=client_id, redirect_uri=redirect_uri):
                report = self.selftest_report()
                report["nexusOAuth"].update(clientId=client_id, redirectUri=redirect_uri)
                r = self.check_report(report, client_id=client_id, redirect_uri=redirect_uri)
                self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_selftest_report_requires_explicit_disabled_metadata(self):
        disabled = self.selftest_report(enabled=False)["nexusOAuth"]
        variants = [None, [], "disabled", {}, {**disabled, "enabled": 0},
                    {**disabled, "configured": 0}, {**disabled, "clientId": "stale"},
                    {**disabled, "redirectUri": PUBLIC_REDIRECT_URI}]
        variants += [{k: v for k, v in disabled.items() if k != missing} for missing in disabled]
        for oauth in variants:
            with self.subTest(oauth=oauth):
                report = self.selftest_report(enabled=False)
                report["nexusOAuth"] = oauth
                r = self.check_report(report, flag="0")
                self.assertNotEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_selftest_report_preserves_release_distribution_and_check_failures(self):
        for distribution in ("direct", "nexus"):
            for key, value in [("ok", False), ("ok", "true"), ("releaseBuild", False),
                    ("releaseBuild", None), ("releaseBuild", 1), ("distribution", "other"),
                    ("buildMarker", None), ("checks", [{"name": "fixture", "ok": False, "detail": "failed"}])]:
                with self.subTest(distribution=distribution, key=key, value=value):
                    report = self.selftest_report(distribution)
                    report[key] = value
                    r = self.check_report(report, distribution)
                    self.assertNotEqual(r.returncode, 0, r.stdout + r.stderr)

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

    def test_kfc_containers_are_game_data_but_text_about_them_is_not(self):
        scanner = release.Scanner([], str(self.dir))
        # the real header: "KFC4" then binary fields (sizes, offsets) with NUL bytes
        scanner.scan_bytes("leak/archive.bin", b"KFC4\xb0\x02A\x00\x0c\x00\x00\x00" + b"\x00" * 64)
        self.assertEqual(scanner.problems, ["leak/archive.bin: game data (a KFC container)"])
        scanner = release.Scanner([], str(self.dir))
        scanner.scan_bytes("docs/review-summary.de.md", "KFC4-Profilanalyse für Enshrouded\n\nSteam-Build 1.".encode())
        scanner.scan_bytes("big.kfc_data", b"x" * 5000)  # the name alone still counts
        self.assertEqual(scanner.problems, ["big.kfc_data: game data (a KFC container)"])

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
from pathlib import Path
with open(os.environ["MOCK_CALLS"], "a") as f: f.write(json.dumps(sys.argv[1:])+"\\n")
if sys.argv[1:3] == ["image", "inspect"] and "--format" in sys.argv: print("sha256:" + "a"*64)
if sys.argv[1] == "run":
    if os.environ.get("MOCK_BLOCK"): time.sleep(30)
    if os.environ.get("MOCK_SELFTEST") and os.environ.get("MOCK_EXIT", "0") == "0":
        for arg in sys.argv:
            if arg.endswith(":/out"):
                Path(arg[:-len(":/out")], "selftest.json").write_text(os.environ["MOCK_SELFTEST"])
    sys.exit(int(os.environ.get("MOCK_EXIT", "0")))
''')
        docker.chmod(0o755)
        self.calls = self.dir / "calls.jsonl"
        return self.release_env(flag="0", client_id=None, redirect_uri=None, base={**os.environ,
            "PATH": f"{bindir}:/usr/bin:/bin", "MOCK_CALLS": str(self.calls),
            "MOCK_SELFTEST": json.dumps(self.selftest_report("direct", enabled=False)),
            "KILN_GLIBC_DIR": str(self.dir / "cache")})

    def script(self, name, *args, env=None):
        return subprocess.run(["bash", str(HERE / "linux-build" / name), *map(str, args)], env=env,
                              cwd=self.dir, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    def test_release_linux_wrapper_forwards_public_oauth_registration(self):
        # Exercise release.sh's actual Linux wrapper through run.sh and a fake Docker client.
        # No release, signing environment, image build or provider request is involved.
        env = self.mock_docker()
        env.update(KILN_NEXUS_ENABLED="0", KILN_NEXUS_OAUTH_CLIENT_ID="unit-test-registration",
                   KILN_NEXUS_OAUTH_REDIRECT_URI="http://127.0.0.1:8089/callback",
                   KILN_LINUX_IMAGE="sha256:" + "a" * 64)
        wrapper = re.search(r"^  linux\(\) \{\n.*?^  \}", (HERE / "release.sh").read_text(), re.M | re.S)
        self.assertIsNotNone(wrapper)
        source, work = self.dir / "source", self.dir / "work"
        source.mkdir(); work.mkdir()
        harness = self.dir / "wrapper.sh"
        harness.write_text('set -euo pipefail\nhere=$1\nsrc=$2\nwork=$3\nlin_target=$4\nlin_rustflags=fixture\n'
                           + wrapper.group() + '\nlinux true\n')
        r = subprocess.run(["bash", str(harness), str(HERE), str(source), str(work), str(self.dir / "target")],
                           cwd=source, env=env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        run = next(json.loads(line) for line in self.calls.read_text().splitlines() if json.loads(line)[0] == "run")
        forwarded = [run[i + 1] for i, arg in enumerate(run[:-1]) if arg == "-e"]
        self.assertIn("KILN_NEXUS_ENABLED", forwarded)
        self.assertIn("KILN_NEXUS_OAUTH_CLIENT_ID", forwarded)
        self.assertIn("KILN_NEXUS_OAUTH_REDIRECT_URI", forwarded)

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
        env["MOCK_SELFTEST"] = json.dumps(self.selftest_report("nexus", enabled=False))
        rel = self.dir / "release"
        (rel / "linux").mkdir(parents=True)
        (rel / "nexus/linux").mkdir(parents=True)
        r = self.script("start-test.sh", rel, "--distribution", "nexus", "--out", self.dir / "results", env=env)
        self.assertEqual(r.returncode, 0, r.stderr)
        calls = [json.loads(l) for l in self.calls.read_text().splitlines()]
        run = next(c for c in calls if c[0] == "run")
        self.assertIn("DISTRIBUTION=nexus", run)
        self.assertIn(f"{rel}:/r:ro", run)

    def test_clean_linux_start_checks_actual_report_against_release_environment(self):
        env = self.mock_docker()
        rel = self.dir / "release"
        (rel / "linux").mkdir(parents=True)
        (rel / "nexus/linux").mkdir(parents=True)
        cases = [("0", False, None, True), ("0", True, None, False),
                 ("1", True, None, True), (None, True, None, True),
                 ("1", False, None, False), (None, False, None, False),
                 ("false", False, None, False), ("", True, None, False),
                 ("1", True, ("clientId", "stale-public-registration"), False),
                 ("1", True, ("redirectUri", "http://127.0.0.1:8090/other"), False),
                 ("0", False, ("releaseBuild", False), False),
                 ("0", False, ("buildMarker", "stale"), False),
                 ("0", False, ("distribution", "other"), False),
                 ("0", False, ("ok", False), False),
                 ("0", False, ("checks", [{"name": "fixture", "ok": False, "detail": "failed"}]), False)]
        for distribution in ("direct", "nexus"):
            for i, (flag, enabled, change, ok) in enumerate(cases):
                with self.subTest(distribution=distribution, flag=flag, enabled=enabled, change=change):
                    report = self.selftest_report(distribution, enabled)
                    if change:
                        target = report["nexusOAuth"] if change[0] in ("clientId", "redirectUri") else report
                        target[change[0]] = change[1]
                    test_env = self.release_env(flag=flag, base=env)
                    test_env["MOCK_SELFTEST"] = json.dumps(report)
                    r = self.script("start-test.sh", rel, "--distribution", distribution,
                                    "--out", self.dir / f"results-{distribution}-{i}", env=test_env)
                    self.assertEqual(r.returncode == 0, ok, r.stdout + r.stderr)

    def test_clean_linux_start_requires_a_report_even_if_container_succeeds(self):
        env = self.mock_docker()
        env["MOCK_SELFTEST"] = ""
        rel = self.dir / "release"
        (rel / "linux").mkdir(parents=True)
        r = self.script("start-test.sh", rel, "--out", self.dir / "results", env=env)
        self.assertNotEqual(r.returncode, 0, r.stdout + r.stderr)

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
