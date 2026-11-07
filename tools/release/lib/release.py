#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Helpers for tools/release/*.sh (docs/kiln/launcher/releasing.md).

  manifest   write an update manifest (latest.json): Tauri's static updater format + Kiln's fields
  sums       write or check SHA256SUMS for a release folder
  verify     check a Tauri updater signature (.sig) against the public key and the version it must be signed for
  deb        print a .deb's control fields and file list (what dpkg-deb -I / -c show), checking them
  scan       look inside every file and archive of a folder for things that must not ship: game data, home paths,
             keys, passwords, tokens
  offset     print where the squashfs of an AppImage starts (for unsquashfs -o)
  glibc      check every Linux ELF of a folder (in the AppImage, deb, rpm and zips too) needs no glibc symbol version
             above a limit (objdump -p version references), so the builds run on older distros (Steam Deck, Ubuntu 22.04)
  updater    look inside launcher builds (AppImage, deb, NSIS setup, an installed exe) for the self-update: its update
             feeds, the updater key, the updater plugin. --absent: the Nexus Mods build must have none of it;
             --present: a direct build must have it all
"""
import argparse
import base64
import hashlib
import io
import json
import os
import re
import shutil
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from datetime import datetime, timezone

# ---------------------------------------------------------------- manifest


def cmd_manifest(a):
    """--entry KEYS=URL|SIG|FILE: KEYS a comma list of updater targets (linux-x86_64-appimage,linux-x86_64)."""
    notes = open(a.notes, encoding="utf-8").read().strip() if a.notes else ""
    platforms = {}
    for e in a.entry:
        keys, rest = e.split("=", 1)
        url, sig, path = rest.split("|")
        entry = {
            "url": url,
            "signature": open(sig, encoding="ascii").read().strip(),
            "sha256": sha256_file(path),
            "size": os.path.getsize(path),
        }
        for k in keys.split(","):
            platforms[k] = entry
    m = {
        "version": a.version,
        "notes": notes,
        "pub_date": a.date or datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "platforms": platforms,
        "kiln": {"format": 1, "channel": a.channel, "page": a.page},
    }
    if a.runtime:
        m["kiln"]["runtime"] = a.runtime
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(m, f, indent=2)
        f.write("\n")


# ---------------------------------------------------------------- sums


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def release_files(root):
    out = []
    for d, dirs, files in os.walk(root):
        dirs.sort()
        for n in sorted(files):
            p = os.path.join(d, n)
            rel = os.path.relpath(p, root).replace(os.sep, "/")
            if rel != "SHA256SUMS":
                out.append(rel)
    return out


def cmd_sums(a):
    path = os.path.join(a.dir, "SHA256SUMS")
    if a.check:
        want = {}
        for line in open(path, encoding="utf-8"):
            h, name = line.rstrip("\n").split("  ", 1)
            want[name] = h
        files = release_files(a.dir)
        bad = [f for f in files if want.get(f) != sha256_file(os.path.join(a.dir, f))]
        missing = sorted(set(want) - set(files))
        extra = sorted(set(files) - set(want))
        for f in bad:
            print(f"SHA256SUMS: {f}: differs", file=sys.stderr)
        for f in missing:
            print(f"SHA256SUMS: {f}: listed, not in the folder", file=sys.stderr)
        for f in extra:
            print(f"SHA256SUMS: {f}: in the folder, not listed", file=sys.stderr)
        if bad or missing or extra:
            sys.exit(1)
        print(f"SHA256SUMS: {len(files)} files match")
        return
    with open(path, "w", encoding="utf-8") as f:
        for rel in release_files(a.dir):
            f.write(f"{sha256_file(os.path.join(a.dir, rel))}  {rel}\n")


# ---------------------------------------------------------------- minisign (Tauri updater signatures)


def _lines(b64text):
    return base64.b64decode(b64text.strip()).decode("utf-8").splitlines()


def verify_sig(data_path, sig_b64, pub_b64, version=None):
    """Returns the trusted comment. Raises ValueError on any mismatch."""
    from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
    from cryptography.exceptions import InvalidSignature

    pub = base64.b64decode(_lines(pub_b64)[1])
    if len(pub) != 42 or pub[:2] != b"Ed":
        raise ValueError("public key: not a minisign Ed25519 key")
    key_id, pk = pub[2:10], Ed25519PublicKey.from_public_bytes(pub[10:])
    lines = _lines(sig_b64)
    if len(lines) < 4 or not lines[2].startswith("trusted comment: "):
        raise ValueError("signature: not a minisign signature")
    sig = base64.b64decode(lines[1])
    alg, sig_key, sig_bytes = sig[:2], sig[2:10], sig[10:]
    if sig_key != key_id:
        raise ValueError(f"signature: made with key {sig_key[::-1].hex().upper()}, not {key_id[::-1].hex().upper()}")
    data = open(data_path, "rb").read()
    if alg == b"ED":
        msg = hashlib.blake2b(data, digest_size=64).digest()
    elif alg == b"Ed":
        msg = data
    else:
        raise ValueError(f"signature: unknown algorithm {alg!r}")
    trusted = lines[2][len("trusted comment: "):]
    try:
        pk.verify(sig_bytes, msg)
        pk.verify(base64.b64decode(lines[3]), sig_bytes + trusted.encode("utf-8"))
    except InvalidSignature:
        raise ValueError("signature: does not match the file")
    if version is not None:
        signed = dict(f.split(":", 1) for f in trusted.split("\t") if ":" in f).get("version")
        if signed != version:
            raise ValueError(f"signature: signed for version {signed!r}, expected {version!r}")
    return trusted


def cmd_verify(a):
    pub = open(a.pubkey, encoding="ascii").read()
    ok = True
    for f in a.files:
        try:
            t = verify_sig(f, open(f + ".sig", encoding="ascii").read(), pub, a.version)
            print(f"signature OK: {os.path.basename(f)} ({t.split(chr(9))[-1]})")
        except (ValueError, OSError) as e:
            print(f"signature FAILED: {f}: {e}", file=sys.stderr)
            ok = False
    sys.exit(0 if ok else 1)


# ---------------------------------------------------------------- .deb (ar + tar)


def ar_members(path):
    with open(path, "rb") as f:
        if f.read(8) != b"!<arch>\n":
            raise ValueError("not an ar archive")
        while True:
            h = f.read(60)
            if len(h) < 60:
                return
            name = h[:16].decode().strip().rstrip("/")
            size = int(h[48:58].decode().strip())
            data = f.read(size)
            if size % 2:
                f.read(1)
            yield name, data


def deb_parts(path):
    control, files = None, []
    for name, data in ar_members(path):
        if name.startswith("control.tar"):
            with tarfile.open(fileobj=io.BytesIO(data)) as t:
                m = next((x for x in t.getmembers() if x.name.lstrip("./") == "control"), None)
                control = t.extractfile(m).read().decode() if m else None
        elif name.startswith("data.tar"):
            with tarfile.open(fileobj=io.BytesIO(data)) as t:
                files = [(m.name, m.size, m.mode) for m in t.getmembers()]
    return control, files


def cmd_deb(a):
    control, files = deb_parts(a.file)
    if not control:
        sys.exit(f"{a.file}: no control file")
    fields = dict(l.split(": ", 1) for l in control.splitlines() if ": " in l and not l.startswith(" "))
    print(control.strip())
    print(f"files: {len(files)}")
    for n, s, _ in files:
        if not n.endswith("/"):
            print(f"  {n} ({s} bytes)")
    errors = []
    # Debian orders "~" before anything: a pre-release 0.2.0-dev.9 may be written 0.2.0~dev.9
    if fields.get("Version") not in (a.version, a.version.replace("-", "~", 1)):
        errors.append(f"Version {fields.get('Version')!r}, expected {a.version!r}")
    if fields.get("Architecture") != "amd64":
        errors.append(f"Architecture {fields.get('Architecture')!r}")
    names = {n.lstrip("./") for n, _, _ in files}
    for need in a.need:
        if need not in names:
            errors.append(f"missing {need}")
    if errors:
        sys.exit("deb: " + "; ".join(errors))
    print("deb OK")


# ---------------------------------------------------------------- AppImage


def appimage_offset(path):
    with open(path, "rb") as f:
        h = f.read(64)
    if h[:4] != b"\x7fELF" or h[4] != 2:
        raise ValueError("not a 64-bit ELF")
    shoff, = struct.unpack_from("<Q", h, 0x28)
    shentsize, shnum = struct.unpack_from("<HH", h, 0x3A)
    return shoff + shentsize * shnum


def cmd_offset(a):
    print(appimage_offset(a.file))


# ---------------------------------------------------------------- scan

# file names that are the game's own data (never shipped), and build leftovers
BAD_NAMES = [
    # (.kfc files are judged by content below: a test fixture may carry the name)
    (re.compile(r"(?i)(^|/)enshrouded(_server)?\.exe$"), "the game's executable"),
    (re.compile(r"(?i)(^|/)steam_api64\.dll$"), "Steam's DLL from the game"),
    (re.compile(r"(?i)\.ksd$"), "a save file"),
    (re.compile(r"(?i)(^|/)(assets-local|editor-assets|admin-assets)/"), "art made from the game"),
    (re.compile(r"(?i)(^|/)\.env$"), "an env file"),
    (re.compile(r"(?i)(^|/)(updater|authenticode)\.(key|env)$"), "a release key or credentials"),
]
BAD_BYTES = [
    # the build machine's home folder (from the environment, so this file does not match itself)
    (re.compile(re.escape(os.path.expanduser("~").encode()) + rb"(?![\w.-])"), "a home path"),
    # a PEM key with its body (libraries that parse PEM carry the bare marker as text)
    (re.compile(rb"-----BEGIN (RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----\r?\n[A-Za-z0-9+/=\r\n:,-]{64,}"), "a private key"),
    # the minisign secret key's header, as text and in base64 (split, so this file does not match itself)
    (re.compile(b"rsign encrypted " + b"secret key"), "a minisign secret key"),
    (re.compile(b"dW50cnVzdGVkIGNvbW1lbnQ6IHJzaWdu" + b"IGVuY3J5cHRlZCBzZWNyZXQga2V5"), "a minisign secret key (base64)"),
    (re.compile(rb"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36}\b"), "a GitHub token"),
    (re.compile(rb"\bgithub_pat_[A-Za-z0-9_]{60,}"), "a GitHub token"),
    (re.compile(rb"\bAKIA[0-9A-Z]{16}\b"), "an AWS key"),
    (re.compile(rb"AZURE_CLIENT_SECRET\s*=\s*\S{8,}"), "an Azure secret"),
    (re.compile(rb"\bxox[baprs]-[A-Za-z0-9-]{10,}"), "a Slack token"),
]


def secrets_from_files(paths):
    """Literal secrets (the updater key's password, credential values) to look for, never printed."""
    out = []
    for p in paths:
        p = os.path.expanduser(p)
        if not os.path.isfile(p):
            continue
        with open(p, "rb") as f:
            text = f.read()
        if p.endswith(".env"):
            for line in text.splitlines():
                if b"=" in line and not line.lstrip().startswith(b"#"):
                    v = line.split(b"=", 1)[1].strip().strip(b"\"'")
                    if len(v) >= 8:
                        out.append(v)
        else:
            v = text.strip()
            if len(v) >= 8 or (v and p.endswith(".password")):
                out.append(v)
                if p.endswith(".key") and _is_b64(v):
                    lines = base64.b64decode(v).splitlines()
                    out.extend(l for l in lines[1:2] if len(l) >= 16)
    return out


def _is_b64(v):
    try:
        base64.b64decode(v, validate=True)
        return True
    except Exception:
        return False


class Scanner:
    def __init__(self, literals, tmp):
        self.literals = literals
        self.tmp = tmp
        self.problems = []
        self.files = 0
        self.archives = 0

    def bad(self, where, what):
        self.problems.append(f"{where}: {what}")

    def scan_bytes(self, where, data):
        self.files += 1
        system_lib = re.search(r"(^|/)usr/lib/[^!]*\.so(\.[0-9]+)*$", where)
        for rx, what in BAD_BYTES:
            if what == "a private key" and system_lib:
                continue  # distro libraries (GnuTLS) carry public self-test keys
            m = rx.search(data)
            if m:
                # Do not print surrounding bytes: even a home-path match may sit next to a password/key.
                self.bad(where, what)
        for lit in self.literals:
            if lit in data:
                self.bad(where, "a release secret (key password or credential)")
        if data[:3] == b"KFC" or (re.search(r"(?i)\.kfc(_data|_resources)?$", where) and len(data) > 4096):
            self.bad(where, "game data (a KFC container)")

    def scan_name(self, where):
        for rx, what in BAD_NAMES:
            if rx.search(where):
                self.bad(where, what)

    def scan_file(self, path, where, depth=0):
        self.scan_name(where)
        data = open(path, "rb").read()
        self.scan_bytes(where, data)
        if depth > 4:
            return
        kind = archive_kind(path, data)
        if kind:
            self.archives += 1
            d = tempfile.mkdtemp(dir=self.tmp)
            try:
                extract(kind, path, d)
            except Exception as e:  # noqa: BLE001
                self.bad(where, f"cannot open the {kind} archive to check it: {e}")
                return
            for base, dirs, files in os.walk(d):
                for n in files:
                    p = os.path.join(base, n)
                    if os.path.islink(p) or not os.path.isfile(p):
                        continue
                    self.scan_file(p, f"{where}!{os.path.relpath(p, d)}", depth + 1)
            shutil.rmtree(d, ignore_errors=True)


def archive_kind(path, data):
    n = path.lower()
    if data[:4] == b"PK\x03\x04" or data[:4] == b"PK\x05\x06":
        return "zip"
    if data[:2] == b"\x1f\x8b" and (n.endswith(".tar.gz") or n.endswith(".tgz")):
        return "tgz"
    if data[:8] == b"!<arch>\n" and n.endswith(".deb"):
        return "deb"
    if data[:4] == b"\xed\xab\xee\xdb":
        return "rpm"
    if data[:4] == b"\x7fELF" and data[8:11] == b"AI\x02":
        return "appimage"
    if data[:2] == b"MZ" and b"Nullsoft" in data[:1 << 20] and n.endswith(".exe"):
        return "nsis"
    return None


def extract(kind, path, dest):
    if kind == "zip":
        with zipfile.ZipFile(path) as z:
            links = []
            for m in z.infolist():
                if m.is_dir():
                    continue
                target = os.path.join(dest, os.path.normpath(m.filename).lstrip("/\\").replace("..", "_"))
                os.makedirs(os.path.dirname(target), exist_ok=True)
                if stat.S_ISLNK(m.external_attr >> 16):
                    links.append((target, os.fsdecode(z.read(m))))
                    continue
                with z.open(m) as src, open(target, "wb") as out:
                    shutil.copyfileobj(src, out)
            # Parents were all created before any links, so no archive write follows a link out of dest.
            for target, link in links:
                os.symlink(link, target)
    elif kind == "tgz":
        with tarfile.open(path) as t:
            t.extractall(dest, filter="data")
    elif kind == "deb":
        for name, data in ar_members(path):
            if name.startswith(("control.tar", "data.tar")):
                sub = os.path.join(dest, name.split(".")[0])
                os.makedirs(sub)
                with tarfile.open(fileobj=io.BytesIO(data)) as t:
                    t.extractall(sub, filter="data")
    elif kind == "appimage":
        off = appimage_offset(path)
        subprocess.run(["unsquashfs", "-q", "-n", "-no-xattrs", "-o", str(off), "-d", os.path.join(dest, "squashfs-root"), path],
                       check=True, stdout=subprocess.DEVNULL)
    elif kind == "rpm":
        # Unwrap by content, not the payload filename (.cpio, .cpio.zstd, or no useful suffix).
        payload = os.path.join(dest, "rpm-payload")
        with open(payload, "wb") as f:
            subprocess.run(["7z", "x", "-so", "--", path], check=True, stdout=f, stderr=subprocess.PIPE)
        for level in range(4):
            with open(payload, "rb") as f:
                head = f.read(8)
            cpio = head.startswith((b"070701", b"070702", b"070707", b"\xc7q", b"q\xc7"))
            if not cpio and not head.startswith((b"\x1f\x8b", b"BZh", b"\xfd7zXZ\x00", b"\x28\xb5\x2f\xfd")):
                raise ValueError("unrecognized RPM payload; expected compressed cpio")
            sub = os.path.join(dest, f"payload-{level}")
            os.mkdir(sub)
            subprocess.run(["7z", "x", "-y", f"-o{sub}", "--", payload], check=True,
                           stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            os.remove(payload)
            if cpio:
                break
            files = os.listdir(sub)
            if len(files) != 1:
                raise ValueError("compressed RPM payload did not yield one cpio archive")
            payload = os.path.join(sub, files[0])
            if os.path.islink(payload) or not os.path.isfile(payload):
                raise ValueError("compressed RPM payload is not a regular file")
        else:
            raise ValueError("too many compression layers in RPM payload")
    elif kind == "nsis":
        subprocess.run(["7z", "x", "-y", f"-o{dest}", path], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def cmd_scan(a):
    literals = secrets_from_files(a.secret_file)
    tmp = tempfile.mkdtemp(prefix="kiln-scan-", dir=a.tmp)
    s = Scanner(literals, tmp)
    try:
        for rel in release_files(a.dir):
            s.scan_file(os.path.join(a.dir, rel), rel)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"scan: {s.files} files checked, {s.archives} archives opened, {len(literals)} literal secrets looked for")
    if s.problems:
        for p in s.problems[:200]:
            print(f"scan: {p}", file=sys.stderr)
        if len(s.problems) > 200:
            print(f"scan: ... {len(s.problems) - 200} more", file=sys.stderr)
        sys.exit(1)
    print("scan: clean")


# ---------------------------------------------------------------- updater (the self-update in a launcher build)

# what the self-update leaves in a launcher binary (launcher/src-tauri/src/selfupdate/feed.rs, tauri.conf.json): the
# update feeds (frostdev.io, GitHub's stable and dev channels), the updater's public key (in the embedded config),
# and the updater plugin's crate (its source paths in panic messages). The Nexus Mods build has none of them.
UPDATE_FEEDS = [
    ("the frostdev.io update feed", b"frostdev.io/kiln/updates"),
    ("the GitHub stable update feed", b"/releases/latest/download/latest.json"),
    ("the GitHub dev update feed", b"/releases/download/updates-dev"),
]
UPDATER_PLUGIN = ("the updater plugin (tauri-plugin-updater)", b"tauri-plugin-updater")
LAUNCHER_BINARIES = ("kiln-launcher", "kiln-launcher.exe")
BUILD_MARKERS = {
    "direct": b"KILN-LAUNCHER-DISTRIBUTION:direct:self-update=1",
    "nexus": b"KILN-LAUNCHER-DISTRIBUTION:nexus:self-update=0",
}


def updater_needles(pubkey_file=None, *, pubkey=None):
    """(what, bytes, binary only): feeds and key anywhere; plugin signatures in native binaries only
    (the bundled THIRD-PARTY-NOTICES.txt names every crate of the workspace)."""
    pub = pubkey if pubkey is not None else open(pubkey_file, "rb").read().strip()
    if not pub:
        raise ValueError("empty updater public key")
    needles = [(w, n, False) for w, n in UPDATE_FEEDS]
    needles.append(("the updater public key (tauri.conf.json)", pub, False))
    try:  # the key itself: the second line of the base64-decoded minisign public key file
        key_line = base64.b64decode(pub).splitlines()[1].strip()
        if len(key_line) >= 40:
            needles.append(("the updater public key (decoded)", key_line, False))
    except Exception:  # noqa: BLE001
        pass
    needles.append((*UPDATER_PLUGIN, True))
    needles.append(("the updater plugin's Rust symbols", b"tauri_plugin_updater", True))
    needles.append(("the updater IPC plugin", b"plugin:updater|", True))
    return needles


def updater_scan(path, label, needles, tmp, depth=0, out=None, root=None, seen=None):
    """{"binaries": [where], "found": [(what, where)]} for one launcher build and everything inside it."""
    out = out if out is not None else {"binaries": [], "found": [], "markers": {}, "problems": []}
    root = os.path.realpath(root or os.path.dirname(os.path.abspath(path)))
    seen = seen if seen is not None else set()
    if os.path.islink(path):
        target = os.path.realpath(path)
        if not target.startswith(root + os.sep) or not os.path.isfile(target):
            out["problems"].append(f"{label}: dangling or external symlink")
            return out
        path = target
    if path in seen:
        return out
    seen.add(path)
    with open(path, "rb") as f:
        data = f.read()
    # Names are not evidence: a text file named kiln-launcher must not make the absence scan pass. Check every
    # native binary for plugin code, including renamed executables and shared libraries.
    is_bin = data.startswith(b"\x7fELF") or data.startswith(b"MZ")
    kind = archive_kind(path, data) or (GlibcScan.archive(path, data[:512]) if not is_bin else None)
    is_launcher = os.path.basename(path).lower() in LAUNCHER_BINARIES and is_bin and kind is None
    if is_launcher:
        out["binaries"].append(label)
        out["markers"][label] = {dist for dist, marker in BUILD_MARKERS.items() if marker in data}
    for what, needle, binary_only in needles:
        if (is_bin or not binary_only) and (needle in data or needle.decode("ascii").encode("utf-16le") in data):
            out["found"].append((what, label))
    if is_bin and BUILD_MARKERS["direct"] in data:
        out["found"].append(("the direct build's compiled self-update feature", label))
    del data
    if kind:
        if depth >= 32:
            out["problems"].append(f"{label}: archive nesting exceeds 32; contents not checked")
            return out
        d = tempfile.mkdtemp(dir=tmp)
        try:
            GlibcScan.extract(kind, path, d)
            for base, dirs, files in os.walk(d):
                dirs.sort()
                for n in dirs:
                    p = os.path.join(base, n)
                    if os.path.islink(p):
                        target = os.path.realpath(p)
                        if not target.startswith(d + os.sep) or not os.path.isdir(target):
                            out["problems"].append(f"{label}!{n}: external directory symlink")
                for n in sorted(files):
                    p = os.path.join(base, n)
                    if not os.path.islink(p) and not os.path.isfile(p):
                        continue
                    updater_scan(p, f"{label}!{os.path.relpath(p, d)}", needles, tmp, depth + 1, out, d, seen)
        except Exception as e:  # noqa: BLE001
            out["problems"].append(f"{label}: cannot inspect {kind}: {e}")
        finally:
            shutil.rmtree(d, ignore_errors=True)
    return out


def updater_problems(r, label, needles, absent):
    problems = list(r["problems"])
    if not r["binaries"]:
        problems.append(f"{label}: no launcher binary ({' or '.join(LAUNCHER_BINARIES)}) found inside: nothing was checked")
    expected = "nexus" if absent else "direct"
    for binary in r["binaries"]:
        if r["markers"][binary] != {expected}:
            problems.append(f"{binary}: missing or conflicting compiled distribution marker (expected {expected})")
        if not absent:
            in_bin = {what for what, where in r["found"] if where == binary}
            # Plugin source paths are a secondary absence check, not guaranteed by optimized Rust builds.
            required = [what for what, _, binary_only in needles if not binary_only and "decoded" not in what]
            problems.extend(f"{binary}: the launcher binary lacks {what}" for what in required if what not in in_bin)
    if absent:
        problems.extend(f"{where}: {what}" for what, where in sorted(set(r["found"])))
    return problems


def cmd_updater(a):
    needles = updater_needles(a.pubkey)
    tmp = tempfile.mkdtemp(prefix="kiln-updater-", dir=a.tmp)
    problems = []
    try:
        for f in a.files:
            r = updater_scan(f, f, needles, tmp)
            found = sorted(set(r["found"]))
            print(f"{f}: launcher binaries {', '.join(r['binaries']) or 'none'}")
            for what, _, _ in needles:
                hits = [w for x, w in found if x == what]
                print(f"  found  {what}: {', '.join(hits[:4])}" if hits else f"  absent {what}")
            problems.extend(updater_problems(r, f, needles, a.absent))
        for d in a.no_sig or []:
            for base, _, files in os.walk(d):
                problems += [f"{os.path.join(base, n)}: an updater signature (.sig) in a build without self-update"
                             for n in files if n.endswith(".sig")]
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if problems:
        for p in problems:
            print(f"updater: {p}", file=sys.stderr)
        sys.exit(1)
    print("updater: " + ("none of the self-update in these builds" if a.absent else "every build has its self-update"))


# ---------------------------------------------------------------- glibc

# glibc's non-numeric version markers and the release that brought them (an unknown one counts as too new)
GLIBC_ABI = {"ABI_DT_RELR": "2.36"}


def _ver(s):
    return tuple(int(x) for x in s.split("."))


def elf_glibc(path):
    """Only version requirements, including symbol-less ABI markers. Definitions and filenames are not needs.
    objdump -p succeeds for static executables too; an objdump error must never turn into an empty success."""
    r = subprocess.run(["objdump", "-p", os.path.abspath(path)], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                       env={**os.environ, "LC_ALL": "C"})
    if r.returncode != 0:
        err = r.stderr.decode(errors="replace").strip()
        return set(), err or f"objdump exit {r.returncode}"
    refs = r.stdout.partition(b"Version References:\n")[2]
    return {m.decode() for m in re.findall(rb"^\s+0x[0-9a-f]+\s+0x[0-9a-f]+\s+\S+\s+GLIBC_(\S+)\s*$",
                                          refs, re.M)}, None


class GlibcScan:
    def __init__(self, limit, tmp):
        self.limit = _ver(limit)
        self.tmp = tmp
        self.rows = []      # (top-level file, max version or "-", ELF count)
        self.problems = []  # "where: what"

    def scan_top(self, path, rel, root=None):
        self.elfs, self.max = 0, None
        self.walk(path, rel, 0, root or os.path.dirname(os.path.abspath(path)))
        self.rows.append((rel, ".".join(map(str, self.max)) if self.max else "-", self.elfs))

    def walk(self, path, where, depth, root):
        if os.path.islink(path):
            target = os.path.realpath(path)
            if not target.startswith(os.path.realpath(root) + os.sep) or not os.path.isfile(target):
                self.problems.append(f"{where}: dangling symlink or target outside the scanned tree")
            else:
                self.walk(target, where, depth, root)
            return
        with open(path, "rb") as f:
            head = f.read(512)
        if head[:4] == b"\x7fELF":
            self.check_elf(path, where)
        kind = self.archive(path, head)
        if not kind:
            return
        if depth >= 32:
            self.problems.append(f"{where}: archive nesting exceeds 32; contents not checked")
            return
        d = tempfile.mkdtemp(dir=self.tmp)
        before = self.elfs
        try:
            try:
                self.extract(kind, path, d)
            except Exception as e:  # noqa: BLE001
                self.problems.append(f"{where}: cannot open the {kind} to check it: {e}")
                return
            for base, dirs, files in os.walk(d):
                dirs.sort()
                for n in dirs:
                    p = os.path.join(base, n)
                    if os.path.islink(p):
                        target = os.path.realpath(p)
                        if (target != d and not target.startswith(d + os.sep)) or not os.path.isdir(target):
                            self.problems.append(f"{where}!{os.path.relpath(p, d)}: dangling or external directory symlink")
                for n in sorted(files):
                    p = os.path.join(base, n)
                    if not os.path.islink(p) and not os.path.isfile(p):
                        continue
                    self.walk(p, f"{where}!{os.path.relpath(p, d)}", depth + 1, d)
            if kind in ("rpm", "deb", "appimage") and self.elfs == before:
                self.problems.append(f"{where}: no ELF files found inside the {kind}")
        finally:
            shutil.rmtree(d, ignore_errors=True)

    @staticmethod
    def archive(path, head):
        if head[:4] == b"\x7fELF" and head[8:11] == b"AI\x02":
            return "appimage"
        if head[:8] == b"!<arch>\n" and path.lower().endswith(".deb"):
            return "deb"
        if head[:4] == b"\xed\xab\xee\xdb":
            return "rpm"
        if head[:4] in (b"PK\x03\x04", b"PK\x05\x06"):
            return "zip"
        if head[257:262] == b"ustar":
            return "tar"
        if head.startswith((b"\x1f\x8b", b"BZh", b"\xfd7zXZ\x00", b"\x28\xb5\x2f\xfd", b"070701", b"070702", b"070707")):
            return "packed"  # gzip/bzip2/xz/zstd and cpio, also inside an RPM
        return None

    @staticmethod
    def extract(kind, path, dest):
        if kind == "tar":
            with tarfile.open(path) as t:
                t.extractall(dest, filter="data")
        elif kind == "packed":
            subprocess.run(["7z", "x", "-y", f"-o{dest}", "--", os.path.abspath(path)], check=True,
                           stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        else:
            # Static inspection: never execute a release binary just to audit its glibc requirements.
            extract(kind, path, dest)

    def check_elf(self, path, where):
        self.elfs += 1
        vers, err = elf_glibc(path)
        if err:
            self.problems.append(f"{where}: objdump -T failed: {err}")
            return
        if "PRIVATE" in vers:
            self.problems.append(f"{where}: needs GLIBC_PRIVATE (tied to one exact glibc build)")
        for v in sorted(vers):
            if v != "PRIVATE" and v not in GLIBC_ABI and not re.fullmatch(r"[0-9]+(?:\.[0-9]+)+", v):
                self.problems.append(f"{where}: needs unknown GLIBC_{v}")
        # (version, name): GLIBC_ABI_DT_RELR counts as 2.36
        nums = sorted(((_ver(GLIBC_ABI.get(v, v)), v if v in GLIBC_ABI else GLIBC_ABI.get(v, v)) for v in vers
                       if v in GLIBC_ABI or re.fullmatch(r"[0-9]+(?:\.[0-9]+)+", v)), reverse=True)
        if not nums:
            return
        if self.max is None or nums[0][0] > self.max:
            self.max = nums[0][0]
        over = [n for v, n in nums if v > self.limit]
        if over:
            self.problems.append(f"{where}: needs GLIBC_{over[0]}" + (f" (also {', '.join(over[1:])})" if over[1:] else ""))


def cmd_glibc(a):
    tmp = tempfile.mkdtemp(prefix="kiln-glibc-", dir=a.tmp)
    s = GlibcScan(a.max, tmp)
    try:
        paths = a.files
        top = os.path.abspath(a.dir)
        if not paths:
            paths = []
            visited = set()
            for base, dirs, files in os.walk(top, followlinks=True):
                dirs.sort()
                resolved = os.path.realpath(base)
                if resolved in visited:
                    dirs[:] = []
                    continue
                visited.add(resolved)
                for n in list(dirs):
                    p = os.path.join(base, n)
                    rel = os.path.relpath(p, top).replace(os.sep, "/")
                    if rel in ("source", "windows", "updates", "nexus/windows"):
                        dirs.remove(n)
                    elif os.path.realpath(p) != os.path.realpath(top) and not os.path.realpath(p).startswith(os.path.realpath(top) + os.sep):
                        s.problems.append(f"{rel}: directory symlink outside the release")
                        dirs.remove(n)
                paths.extend(os.path.join(base, n) for n in sorted(files) if n != "SHA256SUMS")
        for p in paths:
            ap = os.path.abspath(p)
            s.scan_top(p, os.path.relpath(ap, top) if ap.startswith(top + os.sep) else p,
                       top if not a.files else os.path.dirname(ap))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"glibc: limit GLIBC_{a.max}; the newest glibc symbol version each file (and everything inside it) needs:")
    for rel, mx, n in s.rows:
        if n:
            print(f"  {rel:<64} {mx:>6}  ({n} ELF file{'s' if n != 1 else ''})")
    if not any(n for _, _, n in s.rows):
        s.problems.append("no Linux ELF files found")
    if s.problems:
        for p in s.problems[:200]:
            print(f"glibc: {p}", file=sys.stderr)
        if len(s.problems) > 200:
            print(f"glibc: ... {len(s.problems) - 200} more", file=sys.stderr)
        sys.exit(1)
    print("glibc: OK")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    m = sub.add_parser("manifest")
    m.add_argument("--version", required=True)
    m.add_argument("--channel", required=True, choices=["stable", "dev"])
    m.add_argument("--notes")
    m.add_argument("--date")
    m.add_argument("--page", default="https://frostdev.io/kiln")
    m.add_argument("--runtime")
    m.add_argument("--entry", action="append", required=True)
    m.add_argument("--out", required=True)
    m.set_defaults(fn=cmd_manifest)
    s = sub.add_parser("sums")
    s.add_argument("dir")
    s.add_argument("--check", action="store_true")
    s.set_defaults(fn=cmd_sums)
    v = sub.add_parser("verify")
    v.add_argument("--pubkey", required=True)
    v.add_argument("--version")
    v.add_argument("files", nargs="+")
    v.set_defaults(fn=cmd_verify)
    d = sub.add_parser("deb")
    d.add_argument("file")
    d.add_argument("--version", required=True)
    d.add_argument("--need", action="append", default=[])
    d.set_defaults(fn=cmd_deb)
    c = sub.add_parser("scan")
    c.add_argument("dir")
    c.add_argument("--secret-file", action="append", default=[])
    c.add_argument("--tmp")
    c.set_defaults(fn=cmd_scan)
    o = sub.add_parser("offset")
    o.add_argument("file")
    o.set_defaults(fn=cmd_offset)
    u = sub.add_parser("updater")
    u.add_argument("files", nargs="+", help="launcher builds: AppImage, deb, NSIS setup, an installed kiln-launcher(.exe)")
    u.add_argument("--pubkey", required=True, help="the updater public key (base64, as in tauri.conf.json)")
    how = u.add_mutually_exclusive_group(required=True)
    how.add_argument("--absent", action="store_true", help="the Nexus Mods build: none of the self-update may be in it")
    how.add_argument("--present", action="store_true", help="a direct build: its launcher binary has the feeds and the key")
    u.add_argument("--no-sig", action="append", help="a folder that must hold no .sig files (repeatable)")
    u.add_argument("--tmp", default=None)
    u.set_defaults(fn=cmd_updater)
    g = sub.add_parser("glibc")
    g.add_argument("dir", help="a release folder (every file but source/, windows/, updates/, nexus/windows/)")
    g.add_argument("files", nargs="*", help="only these files (paths)")
    g.add_argument("--max", default="2.35", help="the newest GLIBC_ symbol version allowed (default 2.35: Ubuntu 22.04)")
    g.add_argument("--tmp")
    g.set_defaults(fn=cmd_glibc)
    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
