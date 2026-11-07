#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests for the server pack builder (tools/release/lib/serverpack.py): only the server components, the manifest's
paths and bytes, deterministic output, safe names, and refusals (tampered file, unlisted entry, unsafe path).

  python3 -I tools/release/test_serverpack.py
The real-runtime test uses a published runtime zip: KILN_SERVERPACK_RUNTIME (default the 0.2.0-dev.17 copy under
/mnt/Dongus/kiln-wave/coord/rel17/); it is skipped when the file is not there. Its scratch files (the dev.17 pack is
~50 MB, built three times) go to KILN_SERVERPACK_TMP, default /mnt/Dongus/kiln-wave/serverpack when that folder exists,
else TMPDIR.
"""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
import zipfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("serverpack", HERE / "lib" / "serverpack.py")
sp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sp)

TEMPLATE = (HERE / "templates" / "KILN-SERVER-README.txt").read_text(encoding="utf-8")
RUNTIME = Path(os.environ.get("KILN_SERVERPACK_RUNTIME",
                              "/mnt/Dongus/kiln-wave/coord/rel17/kiln-runtime-0.2.0-dev.17.zip"))
SCRATCH = os.environ.get("KILN_SERVERPACK_TMP") or (
    "/mnt/Dongus/kiln-wave/serverpack" if os.path.isdir("/mnt/Dongus/kiln-wave/serverpack") else None)
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
