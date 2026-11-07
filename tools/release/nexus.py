#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Uploads a release folder (tools/release/release.sh) to the Kiln page on Nexus Mods (API v3).

  python3 -I tools/release/nexus.py RELEASE_DIR [--publish] [--sync-description] [--only KIND,...]

Without --publish it is a dry run: it reads the page and its files (GET only) and prints what it would upload, create
and post. With --publish it uploads, creates the files or their new versions, and posts the changelog. The page itself
(its publishing status) is never changed; --sync-description --publish replaces the page's description with
docs/kiln/launcher/nexus-description.bbcode (without --publish it prints the difference).

Files (each a zip: Nexus takes archives; the zip keeps the AppImage's executable bit):
  launcher-windows  main      "Kiln Launcher (Windows)"   the NSIS setup of the Nexus build (nexus/windows/)
  launcher-linux    main      "Kiln Launcher (Linux)"     the AppImage of the Nexus build (nexus/linux/)
  launcher-deb      optional  "Kiln Launcher (Linux .deb)"  the .deb of the Nexus build (nexus/linux/)
  runtime           optional  "Kiln runtime (manual install)"  kiln-runtime zip, kilnctl, kilnctl.exe, INSTALL-RUNTIME.txt,
                                                               kilnctl's systemd units
  server            optional  "Kiln server pack"          runtime/kiln-server-<v>.zip as it is (no zip around it: it is
                                                          the archive server owners unzip into the server folder);
                                                          skipped for a folder without it (releases before the pack)
  sdk               optional  "Kiln SDK (mod authors)"    nupkgs, kiln, kiln.exe, the VS Code extension
The launchers are always the Nexus build: the same launcher without its self-update (Nexus Mods does not allow
programs that update themselves); a folder without nexus/ is refused. Their zips and the SDK's carry nexus/README.txt
(no self-update, new versions on the Kiln page), never the folder's README.txt. A new launcher version archives the
file's previous one (archive_existing_file). The main files set the page's version to the release version
(update_mod_version).

The first release creates the files (POST /mod-files); later ones add versions to them (POST /mod-files/{id}/versions).
Their ids are kept in the state file (default ~/.config/kiln-release/nexus-state.json); a missing id is looked up on
the page by the file's name before a new file is created.

Credentials: NEXUS_API_KEY in ~/.config/kiln-release/nexus.env (--env). The key goes only to the API host, never to
the storage URLs uploads go to, and is never printed or written anywhere.
"""
import argparse
import base64
import difflib
import hashlib
import http.client
import importlib.util
import json
import os
import re
import shutil
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

API = "https://api.nexusmods.com/v3"
MOD_ID = "24958054957357"  # Enshrouded (game 5811), game-scoped id 301
PAGE = "https://www.nexusmods.com/enshrouded/mods/301"
SINGLE_PART_MAX = 100 * 1024 * 1024
CONFIG = os.path.expanduser("~/.config/kiln-release")

KINDS = {
    "launcher-windows": ("main", "Kiln Launcher (Windows)", "The Kiln Launcher for Windows: run the setup (per-user install). New versions come from the Files tab."),
    "launcher-linux": ("main", "Kiln Launcher (Linux)", "The Kiln Launcher for Linux: one AppImage, no install. New versions come from the Files tab."),
    "launcher-deb": ("optional", "Kiln Launcher (Linux .deb)", "The Kiln Launcher as a .deb package for Debian and Ubuntu. New versions come from the Files tab."),
    "runtime": ("optional", "Kiln runtime (manual install)", "The Kiln runtime alone, with kilnctl, for dedicated servers and manual installs."),
    "sdk": ("optional", "Kiln SDK (mod authors)", "The Kiln SDK packages, the kiln command line and the VS Code extension."),
    "server": ("optional", "Kiln server pack", "Kiln for a dedicated server, also a rented one with only FTP: unzip it into "
               "the server folder (the one with enshrouded_server.exe). Steps in KILN-SERVER-README.txt inside."),
}


class Fail(Exception):
    pass


def log(msg):
    print(msg, flush=True)


# ---------------------------------------------------------------- HTTP


class Api:
    def __init__(self, base, key, app_version, dry):
        self.base = base.rstrip("/")
        self.key = key
        self.app_version = app_version
        self.dry = dry
        self.calls = []  # (method, path) for the report

    def _headers(self, extra=None):
        h = {"apikey": self.key, "Application-Name": "kiln-release", "Application-Version": self.app_version,
             "Accept": "application/json", "User-Agent": f"kiln-release/{self.app_version}"}
        h.update(extra or {})
        return h

    def call(self, method, path, body=None, ok=(200, 201, 204)):
        if self.dry and method != "GET":
            raise Fail(f"dry run: refusing {method} {path}")
        self.calls.append((method, path))
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(self.base + path, data=data, method=method,
                                     headers=self._headers({"Content-Type": "application/json"} if data else None))
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                raw = r.read()
                if r.status not in ok:
                    raise Fail(f"{method} {path}: HTTP {r.status}")
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as e:
            detail = e.read()[:600].decode("utf-8", "replace")
            raise Fail(f"{method} {path}: HTTP {e.code}: {detail}") from None
        except urllib.error.URLError as e:
            raise Fail(f"{method} {path}: {e.reason}") from None


def put_presigned(url, data, headers):
    """PUT to a storage URL. No API key: these hosts are not Nexus's API."""
    u = urllib.parse.urlsplit(url)
    conn = (http.client.HTTPSConnection if u.scheme == "https" else http.client.HTTPConnection)(u.netloc, timeout=600)
    path = u.path + ("?" + u.query if u.query else "")
    conn.request("PUT", path, body=data, headers={**headers, "Content-Length": str(len(data))})
    r = conn.getresponse()
    body = r.read()
    headers = {k.lower(): v for k, v in r.getheaders()}
    conn.close()
    if r.status not in (200, 201, 204):
        raise Fail(f"upload to storage: HTTP {r.status}: {body[:300]!r}")
    return headers


def post_presigned(url, data, headers):
    u = urllib.parse.urlsplit(url)
    conn = (http.client.HTTPSConnection if u.scheme == "https" else http.client.HTTPConnection)(u.netloc, timeout=600)
    conn.request("POST", u.path + ("?" + u.query if u.query else ""), body=data, headers={**headers, "Content-Length": str(len(data))})
    r = conn.getresponse()
    body = r.read()
    conn.close()
    if r.status not in (200, 201, 204) or b"<Error>" in body:
        raise Fail(f"completing the multipart upload: HTTP {r.status}: {body[:300]!r}")


# ---------------------------------------------------------------- the files


def release_version(folder):
    with open(os.path.join(folder, "updates", "github", "latest.json"), encoding="utf-8") as f:
        return json.load(f)["version"]


def one(folder, pattern):
    hits = [os.path.join(folder, n) for n in sorted(os.listdir(folder)) if re.fullmatch(pattern, n)] if os.path.isdir(folder) else []
    if len(hits) != 1:
        raise Fail(f"{folder}: expected one file like {pattern}, found {len(hits)}")
    return hits[0]


# the launcher kinds: the Nexus build (nexus/ in the release folder), which has no self-update; they archive the
# file's previous version when they add one (the versions up to 0.2.0-dev.14 had the self-update)
LAUNCHER_KINDS = ("launcher-windows", "launcher-linux", "launcher-deb")


def members(folder, kind, version):
    """The files that go into a kind's zip: (path, name in the zip, executable)."""
    rt, sdk = (os.path.join(folder, d) for d in ("runtime", "sdk"))
    # the launchers come from the Nexus build only, never linux/ or windows/ (those update themselves)
    lin, win = (os.path.join(folder, "nexus", d) for d in ("linux", "windows"))
    readme = os.path.join(folder, "nexus", "README.txt")
    if kind == "launcher-windows":
        return [(one(win, r".*_x64-setup\.exe"), None, False), (readme, "README.txt", False)]
    if kind == "launcher-linux":
        return [(one(lin, r".*\.AppImage"), None, True), (readme, "README.txt", False)]
    if kind == "launcher-deb":
        return [(one(lin, r".*_amd64\.deb"), None, False), (readme, "README.txt", False)]
    if kind == "runtime":
        out = [(os.path.join(rt, f"kiln-runtime-{version}.zip"), None, False), (os.path.join(rt, "INSTALL-RUNTIME.txt"), None, False),
               (os.path.join(rt, "kilnctl"), None, True)]
        if os.path.isfile(os.path.join(rt, "kilnctl.exe")):
            out.append((os.path.join(rt, "kilnctl.exe"), None, False))
        # kilnctl's systemd user units (kiln@.service, kiln-backup@.service, kiln-backup@.timer)
        out += [(os.path.join(rt, n), None, False) for n in sorted(os.listdir(rt)) if n.endswith((".service", ".timer"))]
        return out
    if kind == "server":
        return [(server_pack(folder, version), None, False)]
    if kind == "sdk":
        return [(os.path.join(sdk, n), None, n == "kiln") for n in sorted(os.listdir(sdk))] + [(readme, "README.txt", False)]
    raise Fail(f"unknown kind {kind}")


def server_pack(folder, version):
    return os.path.join(folder, "runtime", f"kiln-server-{version}.zip")


def make_zip(folder, kind, version, out_dir):
    if kind == "server":
        # already the archive to unzip into a server folder: it goes up as it is, never inside another zip
        src = server_pack(folder, version)
        if not os.path.isfile(src):
            raise Fail(f"missing {src}")
        path = os.path.join(out_dir, os.path.basename(src))
        shutil.copyfile(src, path)
        return path
    name = f"kiln-{'runtime-standalone' if kind == 'runtime' else kind}-{version}.zip"
    path = os.path.join(out_dir, name)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for src, arc, exe in members(folder, kind, version):
            if not os.path.isfile(src):
                raise Fail(f"missing {src}")
            info = zipfile.ZipInfo(arc or os.path.basename(src), date_time=(2026, 1, 1, 0, 0, 0))
            info.external_attr = ((0o755 if exe else 0o644) | 0o100000) << 16
            info.compress_type = zipfile.ZIP_STORED if src.endswith((".AppImage", ".zip", ".nupkg", ".vsix", ".exe")) else zipfile.ZIP_DEFLATED
            with open(src, "rb") as f, z.open(info, "w") as dst:
                while chunk := f.read(1 << 20):
                    dst.write(chunk)
    return path


def validate_launcher_archive(path, repo):
    """Validate the immutable upload zip, not just its directory name or the source files before packaging."""
    spec = importlib.util.spec_from_file_location("kiln_release_scan", os.path.join(repo, "tools", "release", "lib", "release.py"))
    scan = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(scan)
    try:
        with open(os.path.join(repo, "launcher", "src-tauri", "tauri.conf.json"), encoding="utf-8") as f:
            pubkey = json.load(f)["plugins"]["updater"]["pubkey"].encode("ascii")
        needles = scan.updater_needles(pubkey=pubkey)
        with tempfile.TemporaryDirectory(prefix="kiln-nexus-check-") as tmp:
            report = scan.updater_scan(path, path, needles, tmp)
            problems = scan.updater_problems(report, path, needles, absent=True)
        with zipfile.ZipFile(path) as z:
            problems.extend(f"{n}: updater signature in Nexus upload" for n in z.namelist() if n.lower().endswith(".sig"))
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as e:
        raise Fail(f"cannot validate Nexus launcher archive {path}: {e}") from e
    if problems:
        raise Fail("Nexus launcher archive rejected: " + "; ".join(problems))


def md5_of(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        while chunk := f.read(1 << 20):
            h.update(chunk)
    return h


# ---------------------------------------------------------------- upload


def upload(api, path, single_max, poll_s=3.0, poll_limit_s=900):
    """Uploads one file through an upload session; returns its id once it is available."""
    size, name = os.path.getsize(path), os.path.basename(path)
    if size <= single_max:
        d = md5_of(path)
        up = api.call("POST", "/uploads", {"size_bytes": size, "filename": name, "md5": d.hexdigest()})["data"]
        with open(path, "rb") as f:
            put_presigned(up["presigned_url"], f.read(), {
                "Content-Disposition": f'attachment; filename="{name}"',
                "Content-MD5": base64.b64encode(d.digest()).decode(),
                "Content-Type": "application/octet-stream",
            })
        how = "single part"
    else:
        up = api.call("POST", "/uploads/multipart", {"size_bytes": size, "filename": name})["data"]
        part, urls = int(up["part_size_bytes"]), up["part_presigned_urls"]
        if part <= 0 or (size + part - 1) // part != len(urls):
            raise Fail(f"multipart: {len(urls)} part URLs for {size} bytes in parts of {part}")
        etags = []
        with open(path, "rb") as f:
            for i, url in enumerate(urls, 1):
                h = put_presigned(url, f.read(part), {})
                etag = h.get("etag")
                if not etag:
                    raise Fail(f"multipart: part {i} came back without an ETag")
                etags.append(etag)
        xml = "<CompleteMultipartUpload>" + "".join(
            f"<Part><PartNumber>{i}</PartNumber><ETag>{e}</ETag></Part>" for i, e in enumerate(etags, 1)) + "</CompleteMultipartUpload>"
        post_presigned(up["complete_presigned_url"], xml.encode(), {"Content-Type": "application/xml"})
        how = f"{len(urls)} parts"
    uid = up["id"]
    api.call("POST", f"/uploads/{uid}/finalise")
    waited = 0.0
    while True:
        state = api.call("GET", f"/uploads/{uid}")["data"]["state"]
        if state == "available":
            break
        if waited >= poll_limit_s:
            raise Fail(f"upload {uid} is still {state!r} after {poll_limit_s:.0f} s")
        time.sleep(poll_s)
        waited += poll_s
    log(f"  uploaded {name} ({size / 1048576:.1f} MB, {how}), upload {uid} available")
    return uid


# ---------------------------------------------------------------- state


def load_state(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return {"format": 1, "files": {}, "releases": {}}


def save_state(path, state):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=2)
        f.write("\n")
    os.replace(tmp, path)


def read_key(env_path):
    if not os.path.isfile(env_path):
        raise Fail(f"no Nexus API key: {env_path} with NEXUS_API_KEY=... is missing")
    with open(env_path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line.startswith("NEXUS_API_KEY="):
                key = line.split("=", 1)[1].strip().strip("\"'")
                if key:
                    return key
    raise Fail(f"no Nexus API key: {env_path} has no NEXUS_API_KEY")


def check_api_url(url):
    u = urllib.parse.urlsplit(url)
    if u.scheme == "https":
        return
    if u.scheme == "http" and u.hostname in ("127.0.0.1", "localhost", "::1"):
        return  # tests
    raise Fail(f"{url}: the API key only goes over https (or to a loopback test server)")


def notes_for(folder, version):
    with open(os.path.join(folder, "RELEASE-NOTES.md"), encoding="utf-8") as f:
        text = f.read().strip()
    text = re.sub(r"^# .*\n+", "", text)
    return (text or f"Kiln {version}.")[:65000]


# ---------------------------------------------------------------- main


def run(argv=None):
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.abspath(os.path.join(here, "..", ".."))
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folder")
    ap.add_argument("--publish", action="store_true", help="really upload and post (default: a dry run with GET calls only)")
    ap.add_argument("--sync-description", action="store_true", help="compare (and with --publish replace) the page's description")
    ap.add_argument("--only", help="comma list of kinds (" + ", ".join(KINDS) + ")")
    ap.add_argument("--mod", default=MOD_ID)
    ap.add_argument("--api", default=API)
    ap.add_argument("--env", default=os.path.join(CONFIG, "nexus.env"))
    ap.add_argument("--state", default=os.path.join(CONFIG, "nexus-state.json"))
    ap.add_argument("--description", default=os.path.join(repo, "docs", "kiln", "launcher", "nexus-description.bbcode"))
    ap.add_argument("--single-part-max", type=int, default=SINGLE_PART_MAX, help=argparse.SUPPRESS)
    ap.add_argument("--poll", type=float, default=3.0, help=argparse.SUPPRESS)
    ap.add_argument("--work", help="where the zips are made (default: RELEASE_DIR.nexus next to the folder)")
    a = ap.parse_args(argv)
    try:
        check_api_url(a.api)
        key = read_key(a.env)
        folder = os.path.abspath(a.folder)
        version = release_version(folder)
        app_version = open(os.path.join(repo, "VERSION"), encoding="utf-8").read().strip()
        api = Api(a.api, key, app_version, dry=not a.publish)
        kinds = [k.strip() for k in a.only.split(",")] if a.only else list(KINDS)
        for k in kinds:
            if k not in KINDS:
                raise Fail(f"unknown kind {k}")
        if any(k in LAUNCHER_KINDS + ("sdk",) for k in kinds) and not os.path.isfile(os.path.join(folder, "nexus", "README.txt")):
            raise Fail(f"{folder} has no Nexus Mods build (nexus/): the launchers on Nexus must not update themselves; "
                       "build the release with tools/release/release.sh")
        win = os.path.join(folder, "nexus", "windows")
        if not (os.path.isdir(win) and any(n.endswith("-setup.exe") for n in os.listdir(win))):
            kinds = [k for k in kinds if k != "launcher-windows"]  # a build without Windows
        if "server" in kinds and not os.path.isfile(server_pack(folder, version)):
            if a.only:
                raise Fail(f"{folder} has no server pack (runtime/kiln-server-{version}.zip)")
            kinds = [k for k in kinds if k != "server"]  # a release from before the server pack

        mode = "PUBLISH" if a.publish else "dry run (GET only)"
        log(f"Nexus Mods: Kiln {version} from {folder} [{mode}]")
        mod = api.call("GET", f"/mods/{a.mod}")["data"]
        log(f"page: {mod.get('name')} (mod {mod.get('id')}, game-scoped {mod.get('game_scoped_id')}), "
            f"status {mod.get('status')}, version field {mod.get('version')!r} -> the main files set it to {version!r}")
        files = api.call("GET", f"/mods/{a.mod}/files")["data"]["mod_files"]
        by_name = {f["name"]: f for f in files}
        log(f"files on the page: {len(files)}" + "".join(f"\n  {f['name']} (id {f['id']}, {f.get('versions_count', '?')} versions)" for f in files))

        state = load_state(a.state)
        if state.get("releases", {}).get(version) and a.publish:
            raise Fail(f"{version} was already uploaded ({a.state}); nothing to do")
        work = a.work or folder.rstrip("/") + ".nexus"
        os.makedirs(work, exist_ok=True)
        plan = []
        for k in kinds:
            category, name, desc = KINDS[k]
            z = make_zip(folder, k, version, work)
            if k in LAUNCHER_KINDS:
                validate_launcher_archive(z, repo)
            # the page's own listing first: its ids are the ones /mod-files/{id} takes (POST /mod-files answers with
            # another id, which 0.2.0-dev.10 stored and dev.11 then could not add a version to)
            fid = (by_name.get(name) or {}).get("id") or state.get("files", {}).get(k)
            size = os.path.getsize(z)
            plan.append((k, category, name, desc, z, fid))
            how = "single part" if size <= a.single_part_max else "multipart"
            target = f"new version of file {fid}" if fid else "a new file"
            if fid and k in LAUNCHER_KINDS:
                target += ", archiving its previous version"
            inside = ", ".join(os.path.relpath(src, folder) for src, _, _ in members(folder, k, version))
            log(f"{k}: {os.path.basename(z)} ({size / 1048576:.1f} MB, {how}, md5 {md5_of(z).hexdigest()}) -> {target}, "
                f"{category}, name {name!r}, version {version!r}" + (", sets the page version" if category == "main" else ""))
            log(f"  from {inside}")
        notes = notes_for(folder, version)
        log(f"changelog for {version}: {len(notes)} characters, first line: {notes.splitlines()[0][:100]!r}")

        if a.sync_description:
            want = open(a.description, encoding="utf-8").read().strip()
            # the site stores line breaks as "<br />" before each line; compare the text itself
            have = re.sub(r"<br\s*/?>", "", mod.get("description") or "").strip()
            if want == have:
                log("description: the page already has nexus-description.bbcode")
            else:
                diff = list(difflib.unified_diff(have.splitlines(), want.splitlines(), "page", "nexus-description.bbcode", lineterm="", n=1))
                log(f"description: {len(diff)} diff lines" + ("\n" + "\n".join(diff[:400]) if diff else ""))

        if not a.publish:
            log("dry run: nothing uploaded or changed. --publish does it.")
            return 0

        for k, category, name, desc, z, fid in plan:
            log(f"{k}:")
            uid = upload(api, z, a.single_part_max, poll_s=a.poll)
            body = {"upload_id": uid, "name": name, "version": version, "description": desc, "file_category": category,
                    "allow_mod_manager_download": False, "primary_mod_manager_download": False,
                    "update_mod_version": category == "main"}
            if fid:
                # a launcher's new version (no self-update) archives the previous one (which may have had it)
                vbody = {**body, "archive_existing_file": True} if k in LAUNCHER_KINDS else body
                r = api.call("POST", f"/mod-files/{fid}/versions", vbody)["data"]
                log(f"  version {r['version']['id']} added to file {fid}" + (", the previous version archived" if k in LAUNCHER_KINDS else ""))
            else:
                r = api.call("POST", "/mod-files", {**body, "mod_id": a.mod})["data"]
                listed = {f["name"]: f for f in api.call("GET", f"/mods/{a.mod}/files")["data"]["mod_files"]}
                fid = (listed.get(name) or {}).get("id") or r["id"]
                log(f"  file {fid} created")
            state.setdefault("files", {})[k] = fid
            save_state(a.state, state)
        api.call("POST", f"/mods/{a.mod}/changelogs", {"version": version, "changelog": notes})
        log(f"changelog posted for {version}")
        if a.sync_description:
            api.call("PATCH", f"/mods/{a.mod}", {"description": open(a.description, encoding="utf-8").read().strip()})
            log("description replaced")
        state.setdefault("releases", {})[version] = {"files": {k: state["files"][k] for k, *_ in plan},
                                                     "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
        save_state(a.state, state)
        log(f"done: {PAGE} (the page's publishing status is unchanged)")
        return 0
    except Fail as e:
        print(f"nexus: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(run())
