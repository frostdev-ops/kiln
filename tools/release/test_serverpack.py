#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests for the server pack builder (tools/release/lib/serverpack.py): only the server components, the manifest's
paths and bytes, deterministic output, safe names, and refusals (tampered file, unlisted entry, unsafe path).

  python3 -I tools/release/test_serverpack.py
The real-runtime test uses a published runtime zip: KILN_SERVERPACK_RUNTIME (default the 0.2.0-dev.17 copy under
/mnt/Dongus/kiln-wave/coord/rel17/); it is skipped when the file is not there. Its scratch files (the dev.17 pack is
~50 MB, built three times) go to KILN_SERVERPACK_TMP, else TMPDIR.
"""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shutil
import stat
import struct
import subprocess
import tarfile
import tempfile
import time
import unittest
from unittest.mock import patch
import zipfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("serverpack", HERE / "lib" / "serverpack.py")
sp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sp)

TEMPLATE = (HERE / "templates" / "KILN-SERVER-README.txt").read_text(encoding="utf-8")
RUNTIME = Path(os.environ.get("KILN_SERVERPACK_RUNTIME",
                              "/mnt/Dongus/kiln-wave/coord/rel17/kiln-runtime-0.2.0-dev.17.zip"))
SCRATCH = os.environ.get("KILN_SERVERPACK_TMP")
EPOCH = 1794072134  # 2026-11-07T17:22:14Z


def runtime_fixture(path, files, components=None, extra_entries=(), tamper=None):
    """A small runtime zip: files {path: (component, bytes)}."""
    components = components or {
        "core": {"sides": ["server", "client"], "default": True, "requires": []},
        "sigdb-server": {"sides": ["server"], "default": True, "requires": ["core"]},
        "sigdb-client": {"sides": ["client"], "default": True, "requires": ["core"]},
        "admin": {"sides": ["server"], "default": True, "requires": ["core"]},
        "emf": {"sides": ["server", "client"], "default": False, "requires": ["core"]},
        "samples": {"sides": ["server"], "default": False, "requires": ["core"]},
    }
    m = {"format": 1, "version": "9.9.9-test+20260101.abcdef0", "components": components,
         "builds": {"server": [{"sha256": "ab" * 32, "steamBuild": "123", "verified": True}]},
         "files": [{"path": p, "size": len(d), "sha256": hashlib.sha256(d).hexdigest(), "component": c, "config": False}
                   for p, (c, d) in sorted(files.items())]}
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("kiln-runtime.json", json.dumps(m))
        for p, (c, d) in sorted(files.items()):
            z.writestr(p, tamper.get(p, d) if tamper else d)
        for name, data in extra_entries:
            z.writestr(name, data)
    return path


FILES = {
    "dinput8.dll": ("core", b"MZ proxy"),
    "kiln/kiln-core.dll": ("core", b"MZ core"),
    "kiln/kiln.json": ("core", b'{"policy": "verified-only"}\n'),
    "kiln/sigdb/server.json": ("sigdb-server", b"{}"),
    "kiln/sigdb/client.json": ("sigdb-client", b"{}"),
    "kiln/mods/kiln-admin/kiln.mod.json": ("admin", b"{}"),
    "kiln/plugins/kiln-emf.dll": ("emf", b"MZ emf"),
    "kiln/mods/hello/Hello.dll": ("samples", b"MZ hello"),
}


class Base(unittest.TestCase):
    scratch = None

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="kiln-serverpack-test-", dir=self.scratch)
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)

    def build(self, runtime, name="pack.zip", epoch=EPOCH):
        out = self.dir / name
        r = sp.build(str(runtime), str(out), TEMPLATE, "9.9.9-test", epoch)
        return out, r


class FixtureTests(Base):
    def rewrite(self, rt, manifest=None, info=None):
        with zipfile.ZipFile(rt) as z:
            entries = [(i, z.read(i)) for i in z.infolist()]
        with zipfile.ZipFile(rt, "w") as z:
            for i, data in entries:
                if manifest and i.filename == sp.MANIFEST:
                    m = json.loads(data)
                    manifest(m)
                    data = json.dumps(m).encode()
                if info:
                    info(i)
                z.writestr(i, data)

    def test_omitted_defaults_match_kiln_manage(self):
        rt = runtime_fixture(self.dir / "rt.zip", FILES)
        self.rewrite(rt, manifest=lambda m: m["components"]["core"].pop("default"))
        # A component with no dependents must also be installed when default is absent.
        self.rewrite(rt, manifest=lambda m: m["components"]["admin"].pop("default"))
        _, result = self.build(rt)
        self.assertEqual(result["components"], ["admin", "core", "sigdb-server"])
        self.rewrite(rt, manifest=lambda m: m["builds"]["server"][0].pop("verified"))
        out, _ = self.build(rt)
        with zipfile.ZipFile(out) as z:
            self.assertIn("Steam build 123", z.read(sp.README).decode())

    def test_windows_collisions_and_file_directory_conflicts_are_refused(self):
        for name in ("DINPUT8.DLL", "d\u0131nput8.dll", "kiln/KILN-CORE.dll", "kiln/sigdb", "KILN-SERVER-README.txt/child"):
            with self.subTest(name=name):
                files = {**FILES, name: ("core", b"x")}
                with self.assertRaises(sp.PackError):
                    self.build(runtime_fixture(self.dir / "rt.zip", files))

    def test_windows_device_aliases_are_refused(self):
        for name in ("kiln/COM\u00b9.txt", "kiln/lpt\u00b2", "kiln/COM\u00b3", "kiln/CON .txt", "kiln/x ",
                     "kiln/" + "x" * 256, "kiln/" + "\U0001f525" * 128, "kiln/\ud800", "x/" * 32 + "file"):
            with self.subTest(name=name):
                self.assertIsNotNone(sp.unsafe_name(name))

    def test_special_and_truncated_entries_are_refused(self):
        for mode in (stat.S_IFLNK, stat.S_IFIFO, stat.S_IFSOCK, stat.S_IFCHR, stat.S_IFBLK, stat.S_IFDIR):
            with self.subTest(mode=mode):
                rt = runtime_fixture(self.dir / "rt.zip", FILES)
                self.rewrite(rt, info=lambda i: setattr(i, "external_attr", (mode | 0o644) << 16))
                with self.assertRaises(sp.PackError):
                    self.build(rt)
        rt = runtime_fixture(self.dir / "rt.zip", FILES)
        with zipfile.ZipFile(rt) as z:
            z.getinfo("dinput8.dll").orig_filename += "\0hidden"
            with self.assertRaises(sp.PackError):
                sp.read_runtime(z)

    def test_archive_limits(self):
        rt = runtime_fixture(self.dir / "rt.zip", FILES)
        for limit, value in (("MAX_ARCHIVE_BYTES", 100), ("MAX_ENTRIES", 3), ("MAX_CENTRAL_BYTES", 100),
                             ("MAX_MANIFEST_BYTES", 100), ("MAX_FILE_BYTES", 4), ("MAX_TOTAL_BYTES", 20)):
            with self.subTest(limit=limit), patch.object(sp, limit, value, create=True):
                with self.assertRaises(sp.PackError):
                    self.build(rt)

    def test_stream_limit_is_enforced_even_when_metadata_lies(self):
        info = zipfile.ZipInfo("dinput8.dll")
        info.file_size = 1
        class Stream:
            def open(self, _):
                return io.BytesIO(b"x" * 10000)
        out = io.BytesIO()
        with self.assertRaises(sp.PackError):
            sp.copy_bounded(Stream(), info, 100, out)
        self.assertLessEqual(len(out.getvalue()), 100)

    def test_zip64_and_large_central_directory_rejected_before_zipfile(self):
        rt = runtime_fixture(self.dir / "rt.zip", FILES)
        original = rt.read_bytes()
        for offset, fmt, value in ((10, "H", 65535), (12, "I", 0x7fffffff)):
            data = bytearray(original)
            end = data.rfind(b"PK\x05\x06")
            struct.pack_into("<" + fmt, data, end + offset, value)
            rt.write_bytes(data)
            with self.subTest(offset=offset), patch.object(sp.zipfile, "ZipFile", side_effect=AssertionError("parsed too soon")):
                with self.assertRaises(sp.PackError):
                    self.build(rt)

    def test_predictable_temporary_symlink_cannot_overwrite_another_file(self):
        rt = runtime_fixture(self.dir / "rt.zip", FILES)
        victim = self.dir / "keep.txt"
        victim.write_bytes(b"keep me")
        (self.dir / "pack.zip.tmp").symlink_to(victim)
        self.build(rt)
        self.assertEqual(victim.read_bytes(), b"keep me")

    def test_archive_permissions_do_not_depend_on_umask(self):
        rt = runtime_fixture(self.dir / "rt.zip", FILES)
        previous = os.umask(0o077)
        try:
            out, _ = self.build(rt)
        finally:
            os.umask(previous)
        self.assertEqual(stat.S_IMODE(out.stat().st_mode), 0o644)

    def test_only_the_server_default_components(self):
        out, r = self.build(runtime_fixture(self.dir / "rt.zip", FILES))
        self.assertEqual(r["components"], ["admin", "core", "sigdb-server"])
        with zipfile.ZipFile(out) as z:
            names = z.namelist()
            self.assertEqual(sorted(names), sorted(
                ["dinput8.dll", "kiln/kiln-core.dll", "kiln/kiln.json", "kiln/sigdb/server.json",
                 "kiln/mods/kiln-admin/kiln.mod.json", sp.README]))
            for n in names:
                if n != sp.README:
                    self.assertEqual(z.read(n), FILES[n][1])
            readme = z.read(sp.README).decode()
        self.assertNotIn("@", readme.replace("@.service", "").replace("@.timer", ""))
        self.assertIn("Kiln server pack 9.9.9-test\n" + "=" * len("Kiln server pack 9.9.9-test") + "\n", readme)
        self.assertIn("Steam build 123", readme)
        self.assertIn("(5 files)", readme)
        self.assertEqual(sp.check(str(out), str(self.dir / "rt.zip"), TEMPLATE, "9.9.9-test", EPOCH), [])

    def test_requires_follow_and_a_new_default_server_component_joins(self):
        comps = {
            "core": {"sides": ["server", "client"], "default": True, "requires": []},
            "base": {"sides": ["server"], "default": False, "requires": []},
            "extra": {"sides": ["server", "client"], "default": True, "requires": ["core", "base"]},
            "clientonly": {"sides": ["client"], "default": True, "requires": ["core"]},
        }
        self.assertEqual(sp.server_components({"components": comps}), ["base", "core", "extra"])
        comps["extra"]["requires"] = ["clientonly"]
        with self.assertRaises(sp.PackError):
            sp.server_components({"components": comps})

    def test_deterministic(self):
        rt = runtime_fixture(self.dir / "rt.zip", FILES)
        a, _ = self.build(rt, "a.zip")
        b, _ = self.build(rt, "b.zip")
        self.assertEqual(a.read_bytes(), b.read_bytes())
        c, _ = self.build(rt, "c.zip", epoch=EPOCH + 86400)
        self.assertNotEqual(a.read_bytes(), c.read_bytes())
        with zipfile.ZipFile(a) as z:
            infos = z.infolist()
        self.assertEqual({i.date_time for i in infos}, {(2026, 11, 7, 17, 22, 14)})
        self.assertEqual([i.filename for i in infos], sorted((i.filename for i in infos), key=str.encode))
        self.assertTrue(all((i.external_attr >> 16) == 0o100644 and i.create_system == 3 and not i.extra for i in infos))

    def test_tampered_file_is_refused(self):
        rt = runtime_fixture(self.dir / "rt.zip", FILES, tamper={"kiln/kiln-core.dll": b"MZ evil!"})
        with self.assertRaisesRegex(sp.PackError, "kiln-core.dll"):
            self.build(rt)
        self.assertFalse((self.dir / "pack.zip").exists())
        self.assertFalse((self.dir / "pack.zip.tmp").exists())

    def test_unlisted_entry_is_refused(self):
        rt = runtime_fixture(self.dir / "rt.zip", FILES, extra_entries=[("kiln/stowaway.dll", b"MZ")])
        with self.assertRaisesRegex(sp.PackError, "does not match"):
            self.build(rt)

    def test_unsafe_paths_are_refused(self):
        for bad in ["../evil.dll", "/abs.dll", "C:/x.dll", "kiln\\x.dll", "kiln//x.dll", "kiln/./x", "kiln/x.",
                    "kiln/con.txt", "kiln/a:b", "kiln/\x01", "kiln/"]:
            self.assertIsNotNone(sp.unsafe_name(bad), bad)
            files = dict(FILES)
            files[bad] = ("core", b"x")
            with self.assertRaises(sp.PackError, msg=bad):
                self.build(runtime_fixture(self.dir / "rt.zip", files))
        for good in ["dinput8.dll", "kiln/dotnet/shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll",
                     "kiln/host/Kiln.Host.deps.json"]:
            self.assertIsNone(sp.unsafe_name(good), good)

    def test_check_finds_a_foreign_file(self):
        rt = runtime_fixture(self.dir / "rt.zip", FILES)
        out, _ = self.build(rt)
        bad = self.dir / "bad.zip"
        with zipfile.ZipFile(out) as src, zipfile.ZipFile(bad, "w") as dst:
            for i in src.infolist():
                dst.writestr(i, src.read(i))
            dst.writestr(zipfile.ZipInfo("kiln/plugins/kiln-emf.dll", date_time=(2026, 11, 7, 17, 22, 14)), b"MZ emf")
        problems = sp.check(str(bad), str(rt), TEMPLATE, "9.9.9-test", EPOCH)
        self.assertTrue(any("kiln-emf.dll: not a server file (component emf)" in p for p in problems), problems)
        self.assertTrue(any("other bytes" in p for p in problems), problems)


class ReleaseWiringTests(Base):
    def staging_functions(self):
        script = (HERE / "github-draft.sh").read_text()
        return script.split("stage=$dir.github\n", 1)[1].split("# the direct build only", 1)[0]

    def test_duplicate_github_asset_cannot_overwrite_a_hardlinked_source(self):
        a, b = self.dir / "a", self.dir / "b"
        a.write_bytes(b"first")
        b.write_bytes(b"second")
        self.addCleanup(shutil.rmtree, str(self.dir) + ".github", True)
        result = subprocess.run(["bash", "-eu", "-c", 'dir=$1; stage=$dir.github\n' + self.staging_functions() +
                                 '\nput "$dir/a" duplicate.zip\nput "$dir/b" duplicate.zip', "test", str(self.dir)],
                                capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(a.read_bytes(), b"first")
        self.assertEqual(Path(str(self.dir) + ".github/duplicate.zip").read_bytes(), b"first")

    def test_systemd_tarball_is_deterministic_and_cannot_replace_an_asset(self):
        script = (HERE / "github-draft.sh").read_text()
        block = "units=()" + script.split("units=()", 1)[1].split('put "$dir/runtime/kilnctl"', 1)[0]
        runtime = self.dir / "runtime"
        runtime.mkdir()
        for unit in ("kiln@.service", "kiln-backup@.service", "kiln-backup@.timer"):
            (runtime / unit).write_bytes((HERE.parent.parent / "runner/systemd" / unit).read_bytes())
        stage = Path(str(self.dir) + ".github")
        self.addCleanup(shutil.rmtree, stage, True)
        setup = ('dir=$1; stage=$dir.github; version=1.2.3; repo_dir=$dir; target=test\n' + self.staging_functions() +
                 '\ngit() { printf "%s\\n" 1794072134; }\n')
        archives = []
        for tz, mask in (("UTC", "022"), ("America/New_York", "077")):
            result = subprocess.run(["bash", "-euo", "pipefail", "-c", setup + f"\numask {mask}\n" + block,
                                     "test", str(self.dir)], capture_output=True,
                                    env={**os.environ, "TZ": tz, **({"POSIXLY_CORRECT": "1"} if mask == "077" else {})})
            self.assertEqual(result.returncode, 0, result.stderr.decode())
            pack = stage / "kiln-systemd-units-1.2.3.tar.gz"
            archives.append(pack.read_bytes())
            with tarfile.open(pack) as tar:
                self.assertEqual(sorted(tar.getnames()), sorted(p.name for p in runtime.iterdir()))
                for entry in tar:
                    self.assertTrue(entry.isfile())
                    self.assertEqual((entry.mode, entry.uid, entry.gid, entry.mtime), (0o644, 0, 0, EPOCH))
                    self.assertEqual(tar.extractfile(entry).read(), (runtime / entry.name).read_bytes())
        self.assertEqual(*archives)
        result = subprocess.run(["bash", "-euo", "pipefail", "-c", setup +
                                 '\nprintf keep >"$stage/kiln-systemd-units-$version.tar.gz"\n' + block,
                                 "test", str(self.dir)], capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((stage / "kiln-systemd-units-1.2.3.tar.gz").read_bytes(), b"keep")


class DocumentationTests(unittest.TestCase):
    def test_relative_doc_links_exist(self):
        root = HERE.parent.parent
        for relative in ("docs/kiln/dedicated-servers.md", "docs/kiln/kilnctl.md", "docs/kiln/launcher/README.md",
                         "docs/kiln/launcher/releasing.md"):
            doc = root / relative
            for target in re.findall(r"\]\(([^\s)]+)\)", doc.read_text()):
                path = target.split("#", 1)[0]
                if path and ":" not in path:
                    self.assertTrue((doc.parent / path).exists(), (relative, target))

    def test_server_instructions_match_boot_and_console_contracts(self):
        root = HERE.parent.parent
        guide = (root / "docs/kiln/dedicated-servers.md").read_text()
        boot = (root / "native/core/src/boot.cpp").read_text()
        console = (root / "managed/services/Kiln.Chat/Console.cs").read_text()
        self.assertIn('"build %s: %s%s%s"', boot)
        self.assertIn('"safe idle: no symbol definitions (%s)"', boot)
        max_lines = re.search(r"MaxLines = (\d+)", console)[1]
        wait = re.search(r"WaitForRunning = TimeSpan.FromSeconds\((\d+)\)", console)[1]
        for text in (guide, TEMPLATE):
            self.assertIn("build <sha256>: verified", text)
            self.assertIn("safe idle: no symbol definitions", text)
            self.assertIn(f"{max_lines} non-empty, non-comment lines", text)
            self.assertIn(f"{wait} seconds", text)
            self.assertIn('"running": true', text)
            self.assertIn("kiln/control/console/", text)
        schema = json.loads((root / "schema/kiln.mod.schema.json").read_text())
        self.assertEqual(schema["properties"]["clients"]["enum"], ["none", "kiln", "mod"])
        for value in schema["properties"]["clients"]["enum"]:
            self.assertIn(f"| `{value}` |", guide)


@unittest.skipUnless(RUNTIME.is_file(), f"no runtime zip at {RUNTIME} (KILN_SERVERPACK_RUNTIME)")
class Dev17Tests(Base):
    """The published 0.2.0-dev.17 runtime: core, sigdb-server and admin; never sigdb-client, emf or samples."""
    scratch = SCRATCH

    @classmethod
    def setUpClass(cls):
        with zipfile.ZipFile(RUNTIME) as z:
            cls.manifest = json.loads(z.read("kiln-runtime.json"))

    def test_dev17_pack(self):
        out, r = self.build(RUNTIME, "kiln-server-0.2.0-dev.17.zip")
        self.assertEqual(r["components"], ["admin", "core", "sigdb-server"])
        server = {f["path"]: f for f in self.manifest["files"] if f["component"] in ("core", "sigdb-server", "admin")}
        with zipfile.ZipFile(out) as z:
            names = z.namelist()
            self.assertEqual(set(names), set(server) | {sp.README})
            self.assertEqual(len(names), len(set(names)))
            for n in names:
                self.assertIsNone(sp.unsafe_name(n), n)
                if n != sp.README:
                    d = z.read(n)
                    self.assertEqual((len(d), hashlib.sha256(d).hexdigest()), (server[n]["size"], server[n]["sha256"]), n)
        for f in self.manifest["files"]:
            if f["component"] in ("sigdb-client", "emf", "samples"):
                self.assertNotIn(f["path"], names)
        for p in ("dinput8.dll", "kiln/kiln-core.dll", "kiln/kiln.json", "kiln/sigdb/server.json",
                  "kiln/mods/kiln-admin/Kiln.Admin.dll", "kiln/host/Kiln.Host.dll"):
            self.assertIn(p, names)
        for p in ("kiln/sigdb/client.json", "kiln/plugins/kiln-emf.dll", "kiln/plugins/kiln-native-hello.dll",
                  "kiln/mods/hello/Hello.dll", "kiln-runtime.json"):
            self.assertNotIn(p, names)
        self.assertTrue(all(n == "dinput8.dll" or n == sp.README or n.startswith("kiln/") for n in names))
        again, _ = self.build(RUNTIME, "again.zip")
        self.assertEqual(out.read_bytes(), again.read_bytes())
        self.assertEqual(sp.check(str(out), str(RUNTIME), TEMPLATE, "9.9.9-test", EPOCH), [])


if __name__ == "__main__":
    unittest.main()
