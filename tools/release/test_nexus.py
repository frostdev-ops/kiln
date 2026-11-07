#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests for nexus.py against a local mock of the Nexus Mods API v3 and its upload storage:
single-part and multipart uploads, finalise and polling, the first release (new files) and the next (new versions),
a file found on the page by name, the description sync, the dry run (GET only) and a missing key. The launchers are
the Nexus build (nexus/ of the release folder, never linux/ or windows/, which update themselves) with nexus/README.txt,
their new versions archive the previous one (archive_existing_file), and a folder without nexus/ is refused.

  python3 -I tools/release/test_nexus.py
"""
import base64
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import re
import shutil
import tempfile
import threading
import unittest
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("nexus", os.path.join(HERE, "nexus.py"))
nexus = importlib.util.module_from_spec(spec)
spec.loader.exec_module(nexus)

KEY = "test-key-not-real-0123456789"
MOD = "4242"


def launcher_fixture(dist, size):
    data = io.BytesIO()
    with zipfile.ZipFile(data, "w") as z:
        z.writestr("usr/bin/kiln-launcher", b"\x7fELF" + bytes(60) +
                   f"KILN-LAUNCHER-DISTRIBUTION:{dist}:self-update={int(dist == 'direct')}".encode() + os.urandom(size))
    return data.getvalue()


def zip_bytes(entries):
    data = io.BytesIO()
    with zipfile.ZipFile(data, "w") as z:
        for name, value in entries.items():
            z.writestr(name, value)
    return data.getvalue()


class Mock:
    def __init__(self):
        self.requests = []  # (method, path, has_key)
        self.uploads = {}
        self.files = []
        self.versions = []
        self.changelogs = []
        self.description = "[b]Old description[/b]\nKiln is a mod framework."
        self.seq = 0
        self.part_size = 1500
        self.polls_before_available = 2
        self.errors = []


def handler_for(mock, base):
    class H(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def _send(self, code, obj=None, headers=None):
            body = b"" if obj is None else json.dumps(obj).encode()
            self.send_response(code)
            for k, v in (headers or {}).items():
                self.send_header(k, v)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _body(self):
            n = int(self.headers.get("Content-Length") or 0)
            return self.rfile.read(n) if n else b""

        def _handle(self):
            path = self.path
            has_key = self.headers.get("apikey") is not None
            mock.requests.append((self.command, path, has_key))
            body = self._body()
            storage = path.startswith("/storage/")
            if storage and has_key:
                mock.errors.append(f"the API key was sent to storage: {path}")
            if not storage:
                if self.headers.get("apikey") != KEY:
                    return self._send(401, {"title": "no key"})
                if self.headers.get("Application-Name") != "kiln-release" or not self.headers.get("Application-Version"):
                    mock.errors.append(f"missing Application-Name/Version on {path}")
            m, p = self.command, path
            if m == "GET" and p == f"/mods/{MOD}":
                return self._send(200, {"data": {"id": MOD, "game_scoped_id": "301", "name": "Kiln", "version": "1.0",
                                                 "status": "not_published", "description": mock.description}})
            if m == "GET" and p == f"/mods/{MOD}/files":
                return self._send(200, {"data": {"mod_files": [{"id": f["id"], "name": f["name"], "versions_count": f["versions"]} for f in mock.files]}})
            if m == "PATCH" and p == f"/mods/{MOD}":
                mock.description = json.loads(body)["description"]
                return self._send(204)
            if m == "POST" and p == "/uploads":
                req = json.loads(body)
                if not re.fullmatch(r"[0-9a-f]{32}", req.get("md5") or ""):
                    return self._send(422, {"title": "md5 required"})
                uid = f"u{len(mock.uploads) + 1}"
                mock.uploads[uid] = {"kind": "single", "req": req, "data": None, "state": "created", "polls": 0}
                return self._send(201, {"data": {"id": uid, "user": {}, "state": "created", "presigned_url": f"{base}/storage/single/{uid}?sig=x"}})
            if m == "POST" and p == "/uploads/multipart":
                req = json.loads(body)
                uid = f"u{len(mock.uploads) + 1}"
                n = (req["size_bytes"] + mock.part_size - 1) // mock.part_size
                mock.uploads[uid] = {"kind": "multi", "req": req, "parts": {}, "data": None, "state": "created", "polls": 0}
                return self._send(201, {"data": {"id": uid, "user": {}, "state": "created", "part_size_bytes": mock.part_size,
                                                 "part_presigned_urls": [f"{base}/storage/part/{uid}/{i}?sig=x" for i in range(1, n + 1)],
                                                 "complete_presigned_url": f"{base}/storage/complete/{uid}?sig=x"}})
            if m == "PUT" and p.startswith("/storage/single/"):
                uid = p.split("/")[3].split("?")[0]
                u = mock.uploads[uid]
                want = f'attachment; filename="{u["req"]["filename"]}"'
                if self.headers.get("Content-Disposition") != want:
                    return self._send(403, {"title": "Content-Disposition does not match the signature"})
                digest = hashlib.md5(body).digest()
                if self.headers.get("Content-MD5") != base64.b64encode(digest).decode() or digest.hex() != u["req"]["md5"].lower():
                    return self._send(400, {"title": "Content-MD5 mismatch"})
                if len(body) != u["req"]["size_bytes"]:
                    return self._send(400, {"title": "size mismatch"})
                u["data"] = body
                return self._send(200)
            if m == "PUT" and p.startswith("/storage/part/"):
                _, _, _, uid, n = p.split("?")[0].split("/")
                mock.uploads[uid]["parts"][int(n)] = body
                return self._send(200, headers={"ETag": f'"{hashlib.md5(body).hexdigest()}"'})
            if m == "POST" and p.startswith("/storage/complete/"):
                uid = p.split("/")[3].split("?")[0]
                u = mock.uploads[uid]
                parts = re.findall(r"<PartNumber>(\d+)</PartNumber><ETag>([^<]+)</ETag>", body.decode())
                if [int(n) for n, _ in parts] != sorted(u["parts"]) or any(e != f'"{hashlib.md5(u["parts"][int(n)]).hexdigest()}"' for n, e in parts):
                    return self._send(400, {"title": "bad CompleteMultipartUpload"})
                u["data"] = b"".join(u["parts"][i] for i in sorted(u["parts"]))
                if len(u["data"]) != u["req"]["size_bytes"]:
                    return self._send(400, {"title": "size mismatch"})
                return self._send(200)
            mf = re.fullmatch(r"/uploads/(u\d+)/finalise", p)
            if m == "POST" and mf:
                u = mock.uploads[mf.group(1)]
                if u["data"] is None:
                    return self._send(422, {"title": "no data"})
                u["finalised"] = True
                return self._send(200, {"data": {"id": mf.group(1), "user": {}, "state": "created"}})
            mg = re.fullmatch(r"/uploads/(u\d+)", p)
            if m == "GET" and mg:
                u = mock.uploads[mg.group(1)]
                u["polls"] += 1
                if u.get("finalised") and u["polls"] > mock.polls_before_available:
                    u["state"] = "available"
                return self._send(200, {"data": {"id": mg.group(1), "user": {}, "state": u["state"]}})
            if m == "POST" and p == "/mod-files":
                req = json.loads(body)
                if mock.uploads[req["upload_id"]]["state"] != "available" or req["mod_id"] != MOD:
                    return self._send(422, {"title": "upload not available"})
                fid = f"F{len(mock.files) + 1}"
                mock.files.append({"id": fid, "name": req["name"], "versions": 1, "req": req})
                # like the real API: the created file's "id" here is not the id /mod-files/{id} takes (the page listing's is)
                return self._send(201, {"data": {"id": f"249580549{len(mock.files):05d}", "game_scoped_id": str(len(mock.files)), "name": req["name"],
                                                 "file_category": req["file_category"]}})
            mv = re.fullmatch(r"/mod-files/(F\d+)/versions", p)
            if m == "POST" and mv:
                req = json.loads(body)
                f = next((f for f in mock.files if f["id"] == mv.group(1)), None)
                if not f or mock.uploads[req["upload_id"]]["state"] != "available":
                    return self._send(404, {"title": "no such file"})
                if "archive_existing_file" in req and not isinstance(req["archive_existing_file"], bool):
                    return self._send(422, {"title": "archive_existing_file must be a boolean"})
                if req.get("archive_existing_file"):
                    f["archived"] = f.get("archived", 0) + 1  # the API: the version this one replaces is archived
                f["versions"] += 1
                mock.versions.append((f["id"], req))
                return self._send(201, {"data": {"file": {"id": f["id"], "game_scoped_id": "1", "name": f["name"], "file_category": req["file_category"]},
                                                 "version": {"id": f"V{len(mock.versions)}", "position": "2"}}})
            if m == "POST" and p == f"/mods/{MOD}/changelogs":
                mock.changelogs.append(json.loads(body))
                return self._send(201, {"data": {}})
            return self._send(404, {"title": f"mock: no route {m} {p}"})

        do_GET = do_POST = do_PUT = do_PATCH = _handle

    return H


class NexusTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="nexus-test-")
        self.mock = Mock()
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), None)
        base = f"http://127.0.0.1:{self.server.server_address[1]}"
        self.server.RequestHandlerClass = handler_for(self.mock, base)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.base = base
        self.env = os.path.join(self.tmp, "nexus.env")
        with open(self.env, "w") as f:
            f.write(f"NEXUS_API_KEY={KEY}\n")
        self.state = os.path.join(self.tmp, "state.json")
        self.desc = os.path.join(self.tmp, "nexus-description.bbcode")
        with open(self.desc, "w") as f:
            f.write("[b]Kiln[/b]\nKiln is a mod framework and launcher for Enshrouded.\n")

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def release(self, version, nexus=True, server=False):
        d = os.path.join(self.tmp, f"kiln-{version}")
        files = {
            "updates/github/latest.json": json.dumps({"version": version}).encode(),
            "README.txt": b"Kiln readme: the launcher updates itself\n",
            "RELEASE-NOTES.md": f"# Kiln {version}\n\n- Self-update.\n- Fixes.\n".encode(),
            # the direct build (updates itself): never for Nexus
            f"linux/kiln-launcher_{version}_amd64.AppImage": launcher_fixture("direct", 5000),
            f"linux/kiln-launcher_{version}_amd64.AppImage.sig": b"sig",
            f"linux/kiln-launcher_{version}_amd64.deb": launcher_fixture("direct", 3000),
            f"windows/kiln-launcher_{version}_x64-setup.exe": launcher_fixture("direct", 1200),
            f"windows/kiln-launcher_{version}_x64-setup.exe.sig": b"sig",
            f"runtime/kiln-runtime-{version}.zip": os.urandom(900),
            "runtime/INSTALL-RUNTIME.txt": b"install\n",
            "runtime/kilnctl": os.urandom(400),
            "runtime/kilnctl.exe": os.urandom(400),
            "sdk/Kiln.Sdk.0.2.0.nupkg": os.urandom(300),
            "sdk/kiln": os.urandom(300),
        }
        if nexus:  # the Nexus build: no self-update
            files.update({
                f"nexus/linux/kiln-launcher_{version}_amd64.AppImage": launcher_fixture("nexus", 5000),
                f"nexus/linux/kiln-launcher_{version}_amd64.deb": launcher_fixture("nexus", 3000),
                f"nexus/windows/kiln-launcher_{version}_x64-setup.exe": launcher_fixture("nexus", 1200),
                "nexus/README.txt": b"Kiln (Nexus Mods): this build does not update itself\n",
            })
        if server:  # the server pack and kilnctl's systemd units (releases since the pack)
            files.update({
                f"runtime/kiln-server-{version}.zip": zip_bytes({"dinput8.dll": b"MZ", "KILN-SERVER-README.txt": b"readme"}),
                "runtime/kiln@.service": b"[Service]\n",
                "runtime/kiln-backup@.service": b"[Service]\n",
                "runtime/kiln-backup@.timer": b"[Timer]\n",
            })
        for rel, data in files.items():
            p = os.path.join(d, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "wb") as f:
                f.write(data)
        return d

    def run_nexus(self, *args, env=None):
        out, err = io.StringIO(), io.StringIO()
        argv = [*args, "--api", self.base, "--mod", MOD, "--env", env or self.env, "--state", self.state,
                "--description", self.desc, "--single-part-max", "4000", "--poll", "0.01"]
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = nexus.run(argv)
        return code, out.getvalue(), err.getvalue()

    def test_missing_key_fails_cleanly(self):
        code, out, err = self.run_nexus(self.release("0.2.0-dev.9"), env=os.path.join(self.tmp, "absent.env"))
        self.assertEqual(code, 2)
        self.assertIn("no Nexus API key", err)
        self.assertEqual(self.mock.requests, [])
        with open(self.env, "w") as f:
            f.write("OTHER=1\n")
        code, _, err = self.run_nexus(self.release("0.2.0-dev.9"))
        self.assertEqual(code, 2)
        self.assertIn("has no NEXUS_API_KEY", err)

    def test_key_only_over_https_or_loopback(self):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = nexus.run([self.release("0.2.0-dev.9"), "--api", "http://api.example.com/v3", "--env", self.env])
        self.assertEqual(code, 2)
        self.assertIn("only goes over https", err.getvalue())

    def test_dry_run_reads_only(self):
        code, out, _ = self.run_nexus(self.release("0.2.0-dev.9"), "--sync-description")
        self.assertEqual(code, 0, out)
        self.assertTrue(all(m == "GET" for m, _, _ in self.mock.requests), self.mock.requests)
        self.assertIn("dry run: nothing uploaded", out)
        self.assertIn("a new file", out)
        self.assertIn("launcher-linux: kiln-launcher-linux-0.2.0-dev.9.zip", out)
        self.assertIn("multipart", out)
        self.assertIn("+Kiln is a mod framework and launcher for Enshrouded.", out)
        self.assertIn("version field '1.0'", out)
        self.assertFalse(os.path.exists(self.state))
        self.assertNotIn(KEY, out)

    def test_first_release_creates_files_then_the_next_adds_versions(self):
        code, out, err = self.run_nexus(self.release("0.2.0-dev.9"), "--publish")
        self.assertEqual(code, 0, out + err)
        self.assertEqual(self.mock.errors, [])
        kinds = ["Kiln Launcher (Windows)", "Kiln Launcher (Linux)", "Kiln Launcher (Linux .deb)", "Kiln runtime (manual install)",
                 "Kiln SDK (mod authors)"]
        self.assertEqual([f["name"] for f in self.mock.files], kinds)
        cats = {f["name"]: f["req"]["file_category"] for f in self.mock.files}
        self.assertEqual(cats["Kiln Launcher (Linux)"], "main")
        self.assertEqual(cats["Kiln runtime (manual install)"], "optional")
        self.assertTrue(all(f["req"]["update_mod_version"] == (cats[f["name"]] == "main") for f in self.mock.files))
        self.assertTrue(all(f["req"]["version"] == "0.2.0-dev.9" for f in self.mock.files))
        kinds_of_upload = [u["kind"] for u in self.mock.uploads.values()]
        self.assertIn("multi", kinds_of_upload)
        self.assertIn("single", kinds_of_upload)
        self.assertTrue(all(u["polls"] > self.mock.polls_before_available for u in self.mock.uploads.values()))
        # the uploaded AppImage zip keeps the executable bit
        linux = next(u for u in self.mock.uploads.values() if u["req"]["filename"].startswith("kiln-launcher-linux"))
        import zipfile
        z = zipfile.ZipFile(io.BytesIO(linux["data"]))
        app = next(i for i in z.infolist() if i.filename.endswith(".AppImage"))
        self.assertEqual((app.external_attr >> 16) & 0o777, 0o755)
        self.assertEqual(self.mock.changelogs, [{"version": "0.2.0-dev.9", "changelog": "- Self-update.\n- Fixes."}])
        with open(self.state) as f:
            state = json.load(f)
        self.assertEqual(sorted(state["files"].values()), ["F1", "F2", "F3", "F4", "F5"])
        self.assertIn("0.2.0-dev.9", state["releases"])
        self.assertNotIn(KEY, out + json.dumps(state))
        # the same version again: refused
        code, _, err = self.run_nexus(self.release("0.2.0-dev.9"), "--publish")
        self.assertEqual(code, 2)
        self.assertIn("already uploaded", err)
        # the next release: versions of the same five files, no new file
        code, out, err = self.run_nexus(self.release("0.2.0-dev.10"), "--publish")
        self.assertEqual(code, 0, out + err)
        self.assertEqual(len(self.mock.files), 5)
        self.assertEqual(sorted(fid for fid, _ in self.mock.versions), ["F1", "F2", "F3", "F4", "F5"])
        self.assertTrue(all(req["version"] == "0.2.0-dev.10" for _, req in self.mock.versions))
        self.assertEqual(self.mock.errors, [])

    def zip_of(self, prefix):
        import zipfile
        u = next(u for u in self.mock.uploads.values() if u["req"]["filename"].startswith(prefix))
        z = zipfile.ZipFile(io.BytesIO(u["data"]))
        return {i.filename: z.read(i) for i in z.infolist()}

    def test_launchers_are_the_nexus_build_and_archive_the_previous_version(self):
        rel = self.release("0.2.0-dev.9")
        code, out, err = self.run_nexus(rel, "--publish")
        self.assertEqual(code, 0, out + err)
        # every launcher zip holds the Nexus build and its README, nothing of the direct build (no .sig either)
        for prefix, name in [("kiln-launcher-linux", "kiln-launcher_0.2.0-dev.9_amd64.AppImage"),
                             ("kiln-launcher-deb", "kiln-launcher_0.2.0-dev.9_amd64.deb"),
                             ("kiln-launcher-windows", "kiln-launcher_0.2.0-dev.9_x64-setup.exe")]:
            files = self.zip_of(prefix)
            self.assertEqual(sorted(files), sorted([name, "README.txt"]), prefix)
            with open(os.path.join(rel, "nexus", "linux" if "windows" not in prefix else "windows", name), "rb") as f:
                self.assertEqual(files[name], f.read(), prefix)
            self.assertIn(b"KILN-LAUNCHER-DISTRIBUTION:nexus:self-update=0", files[name], prefix)
            self.assertIn(b"does not update itself", files["README.txt"], prefix)
        # the SDK zip carries the Nexus README too; the runtime is as before
        self.assertIn(b"does not update itself", self.zip_of("kiln-sdk")["README.txt"])
        self.assertIn("kilnctl", self.zip_of("kiln-runtime-standalone"))
        # the first release creates the files: nothing to archive yet
        self.assertTrue(all("archive_existing_file" not in f["req"] for f in self.mock.files))
        self.assertIn("from nexus/linux/kiln-launcher_0.2.0-dev.9_amd64.AppImage, nexus/README.txt", out)
        # the next release: the launchers' new versions archive the previous ones; the runtime and SDK do not
        code, out, err = self.run_nexus(self.release("0.2.0-dev.10"), "--publish")
        self.assertEqual(code, 0, out + err)
        by_file = {next(f["name"] for f in self.mock.files if f["id"] == fid): req for fid, req in self.mock.versions}
        for name in ["Kiln Launcher (Windows)", "Kiln Launcher (Linux)", "Kiln Launcher (Linux .deb)"]:
            self.assertIs(by_file[name]["archive_existing_file"], True, name)
        for name in ["Kiln runtime (manual install)", "Kiln SDK (mod authors)"]:
            self.assertNotIn("archive_existing_file", by_file[name], name)
        self.assertEqual(sorted(f["name"] for f in self.mock.files if f.get("archived")),
                         ["Kiln Launcher (Linux .deb)", "Kiln Launcher (Linux)", "Kiln Launcher (Windows)"])
        self.assertIn("the previous version archived", out)
        self.assertEqual(self.mock.errors, [])

    def test_the_dry_run_says_what_it_would_archive(self):
        self.mock.files.append({"id": "F7", "name": "Kiln Launcher (Linux)", "versions": 3, "req": {}})
        self.mock.files.append({"id": "F8", "name": "Kiln runtime (manual install)", "versions": 3, "req": {}})
        code, out, _ = self.run_nexus(self.release("0.2.0-dev.9"), "--only", "launcher-linux,runtime")
        self.assertEqual(code, 0, out)
        self.assertIn("new version of file F7, archiving its previous version", out)
        self.assertIn("new version of file F8, optional", out)
        self.assertTrue(all(m == "GET" for m, _, _ in self.mock.requests))

    def test_a_folder_without_the_nexus_build_is_refused(self):
        code, out, err = self.run_nexus(self.release("0.2.0-dev.9", nexus=False), "--publish")
        self.assertEqual(code, 2)
        self.assertIn("no Nexus Mods build (nexus/)", err)
        self.assertEqual([m for m, _, _ in self.mock.requests if m != "GET"], [])
        self.assertEqual(self.mock.uploads, {})
        # the runtime alone needs no launcher build
        code, out, err = self.run_nexus(self.release("0.2.0-dev.9", nexus=False), "--only", "runtime")
        self.assertEqual(code, 0, out + err)

    def test_a_file_already_on_the_page_gets_a_version(self):
        self.mock.files.append({"id": "F7", "name": "Kiln Launcher (Linux)", "versions": 1, "req": {}})
        code, out, err = self.run_nexus(self.release("0.2.0-dev.9"), "--publish", "--only", "launcher-linux")
        self.assertEqual(code, 0, out + err)
        self.assertEqual([fid for fid, _ in self.mock.versions], ["F7"])
        self.assertEqual(len(self.mock.files), 1)

    def test_the_server_pack_goes_up_as_it_is(self):
        rel = self.release("0.2.0-dev.9", server=True)
        code, out, err = self.run_nexus(rel, "--publish")
        self.assertEqual(code, 0, out + err)
        self.assertEqual(self.mock.errors, [])
        cats = {f["name"]: f["req"]["file_category"] for f in self.mock.files}
        self.assertEqual(cats["Kiln server pack"], "optional")
        self.assertEqual(len(self.mock.files), 6)
        server = next(u for u in self.mock.uploads.values() if u["req"]["filename"].startswith("kiln-server"))
        self.assertEqual(server["req"]["filename"], "kiln-server-0.2.0-dev.9.zip")
        with open(os.path.join(rel, "runtime", "kiln-server-0.2.0-dev.9.zip"), "rb") as f:
            self.assertEqual(server["data"], f.read())  # the pack itself, not a zip around it
        runtime = self.zip_of("kiln-runtime-standalone")
        for unit in ("kiln@.service", "kiln-backup@.service", "kiln-backup@.timer"):
            self.assertIn(unit, runtime)
        # a folder without the pack: skipped, unless asked for by name
        code, out, err = self.run_nexus(self.release("0.2.0-dev.10"), "--only", "server")
        self.assertEqual(code, 2)
        self.assertIn("has no server pack", err)
        code, out, err = self.run_nexus(self.release("0.2.0-dev.10"))
        self.assertEqual(code, 0, out + err)
        self.assertNotIn("server:", out)

    def test_sync_description_publish(self):
        code, out, err = self.run_nexus(self.release("0.2.0-dev.9"), "--publish", "--sync-description", "--only", "sdk")
        self.assertEqual(code, 0, out + err)
        with open(self.desc) as f:
            self.assertEqual(self.mock.description, f.read().strip())
        self.assertIn("description replaced", out)


if __name__ == "__main__":
    unittest.main(verbosity=2)
