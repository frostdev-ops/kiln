#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The Kiln server pack (docs/kiln/dedicated-servers.md): the runtime bundle's server files as a flat zip that is a
working install once unzipped into a dedicated server's folder (dinput8.dll next to enshrouded_server.exe, kiln/),
for hosts where the owner has only FTP or a file manager.

  serverpack.py build RUNTIME_ZIP --out PACK_ZIP --readme TEMPLATE --version V [--epoch S]
  serverpack.py check PACK_ZIP --runtime RUNTIME_ZIP --readme TEMPLATE --version V [--epoch S]

The files are the components `kilnctl deploy --bundle` installs on a server (kiln_manage::install::select_components
for the server side): every component of kiln-runtime.json whose sides hold "server" and whose default is true, plus
what they require. Each keeps its game-relative path from the manifest and is checked against the manifest's size and
sha256 while it is copied. KILN-SERVER-README.txt (the template with @VERSION@, @RULE@, @RUNTIME_VERSION@,
@COMPONENTS@, @FILE_COUNT@, @BUILDS@ filled in) sits at the zip's root. kiln-runtime.json itself stays out: unzipped, it would only
be a stray file in the server folder.

Deterministic like the runtime zip (bundle.md): entries sorted bytewise by name, one fixed time (SOURCE_DATE_EPOCH,
or --epoch), deflate level 6, mode 0644, Unix "made by", no directory entries, no extra fields. The same runtime zip,
template and epoch give the same bytes. `check` builds it again next to the pack and compares the bytes, then checks
every entry (only the server components, paths, sizes and hashes from the manifest, safe names, sorted, one time).
"""
import argparse
from contextlib import contextmanager
import hashlib
import io
import json
import os
import re
import stat
import struct
import sys
import tempfile
import time
import zipfile

README = "KILN-SERVER-README.txt"
MANIFEST = "kiln-runtime.json"
SIDE = "server"
ZIP_EPOCH_MIN = 315532800  # 1980-01-01: the earliest time a zip entry can hold
# Release bundles are currently ~53 MiB compressed / 125 MiB expanded. Bound metadata before ZipFile allocates
# its entry table, and bound every expansion (including components that won't be copied).
MAX_ARCHIVE_BYTES = 512 << 20
MAX_CENTRAL_BYTES = 16 << 20
MAX_ENTRIES = 10000
MAX_MANIFEST_BYTES = 4 << 20
MAX_FILE_BYTES = 256 << 20
MAX_TOTAL_BYTES = 1 << 30


class PackError(Exception):
    pass


def unsafe_name(name):
    """Why an entry name must not be unzipped into a server folder, or None. Zip names use '/' only."""
    if not isinstance(name, str) or not name or name.endswith("/"):
        return "a directory or empty name"
    if "\\" in name:
        return "a backslash"
    if name.startswith("/") or re.match(r"^[A-Za-z]:", name):
        return "an absolute path"
    if any(ord(c) < 0x20 or c == "\x7f" for c in name):
        return "a control character"
    if re.search(r'[<>:"|?*]', name):
        return "a character Windows does not allow in names"
    parts = name.split("/")
    if len(parts) > 32:
        return "more than 32 path parts"
    try:
        if any(len(p.encode("utf-16-le")) > 510 for p in parts):
            return "a part longer than 255 UTF-16 code units"
    except UnicodeEncodeError:
        return "invalid Unicode"
    if any(p in ("", ".", "..") for p in parts):
        return "an empty, '.' or '..' path part"
    if any(p != p.rstrip(" .") for p in parts):
        return "a part ending in a space or dot (Windows drops it)"
    if any(re.fullmatch(r"(?i)(con|prn|aux|nul|com[0-9¹²³]|lpt[0-9¹²³])", p.split(".")[0].rstrip(" ")) for p in parts):
        return "a reserved Windows device name"
    return None


def check_paths(names):
    """Refuse Windows aliases and file/directory conflicts, including against generated root files."""
    files, dirs = set(), set()
    for name in names:
        why = unsafe_name(name)
        if why:
            raise PackError(f"unsafe path {name!r}: {why}")
        # Windows compares with an uppercase table (notably dotless i); casefold also conservatively refuses
        # expanding Unicode aliases. ASCII-only runtime paths have exactly the same keys under either operation.
        parts = name.upper().casefold().split("/")
        key = "/".join(parts)
        parents = ["/".join(parts[:i]) for i in range(1, len(parts))]
        if key in files or key in dirs or any(p in files for p in parents):
            raise PackError(f"Windows path collision: {name}")
        files.add(key)
        dirs.update(parents)


@contextmanager
def open_zip(path):
    """Only bounded, single-disk classic ZIPs; inspect the central table before ZipFile parses it."""
    with open(path, "rb") as raw:
        size = os.fstat(raw.fileno()).st_size
        if size > MAX_ARCHIVE_BYTES:
            raise PackError("zip exceeds archive byte limit")
        raw.seek(max(0, size - 65557))
        tail = raw.read(65557)
        end = tail.rfind(b"PK\x05\x06")
        if end < 0 or len(tail) - end < 22:
            raise PackError("zip has no end record")
        _, disk, cd_disk, on_disk, count, cd_size, cd_offset, comment = struct.unpack_from("<4s4H2IH", tail, end)
        if end + 22 + comment != len(tail):
            raise PackError("invalid zip end record")
        if count == 65535 or cd_size == 0xffffffff or cd_offset == 0xffffffff or tail[max(0, end - 20):end - 16] == b"PK\x06\x07":
            raise PackError("ZIP64 is not supported for release packs")
        if disk or cd_disk or on_disk != count:
            raise PackError("multi-disk zip is not supported")
        if count > MAX_ENTRIES or cd_size > MAX_CENTRAL_BYTES:
            raise PackError("zip exceeds entry or central-directory limit")
        if cd_offset + cd_size != size - len(tail) + end:
            raise PackError("invalid zip central-directory bounds")
        raw.seek(cd_offset)
        central = raw.read(cd_size)
        offset = actual = 0
        while offset < len(central):
            if central[offset:offset + 4] != b"PK\x01\x02" or offset + 46 > len(central):
                raise PackError("invalid zip central-directory entry")
            lengths = struct.unpack_from("<3H", central, offset + 28)
            offset += 46 + sum(lengths)
            actual += 1
            if actual > MAX_ENTRIES:
                raise PackError("zip exceeds entry limit")
        if offset != cd_size or actual != count:
            raise PackError("invalid zip central-directory count")
        raw.seek(0)
        with zipfile.ZipFile(raw) as z:
            infos = z.infolist()
            if any(i.file_size > MAX_FILE_BYTES for i in infos) or sum(i.file_size for i in infos) > MAX_TOTAL_BYTES:
                raise PackError("zip exceeds expanded byte limit")
            if any(i.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED) or i.flag_bits & 1 for i in infos):
                raise PackError("zip uses encryption or unsupported compression")
            yield z


def copy_bounded(z, info, limit, out=None):
    """Stream and hash within the expected size, even if the ZIP metadata lies."""
    if info.file_size > limit:
        raise PackError(f"{info.filename}: exceeds size limit")
    size, digest = 0, hashlib.sha256()
    with z.open(info) as src:
        while data := src.read(min(1 << 16, limit - size + 1)):
            size += len(data)
            if size > limit:
                raise PackError(f"{info.filename}: exceeds size limit")
            digest.update(data)
            if out is not None:
                out.write(data)
    return size, digest.hexdigest()


def server_components(manifest):
    """The components kilnctl deploy --bundle installs on a server: the defaults of the side plus what they require."""
    comps = manifest.get("components") or {}
    want = sorted(n for n, c in comps.items() if c.get("default", True) and SIDE in (c.get("sides") or []))
    i = 0
    while i < len(want):
        for r in comps[want[i]].get("requires") or []:
            if r not in comps:
                raise PackError(f"component {want[i]} requires unknown component {r}")
            if SIDE not in (comps[r].get("sides") or []):
                raise PackError(f"component {want[i]} requires {r}, which is not for the server")
            if r not in want:
                want.append(r)
        i += 1
    if not want:
        raise PackError("the runtime has no default server components")
    return sorted(want)


def zip_time(epoch):
    t = time.gmtime(max(int(epoch), ZIP_EPOCH_MIN))
    return (t.tm_year, t.tm_mon, t.tm_mday, t.tm_hour, t.tm_min, t.tm_sec - t.tm_sec % 2)


def read_runtime(z):
    """The manifest of an open runtime zip, after checking that its entries are exactly the manifest's files."""
    names = [i.filename for i in z.infolist()]
    if MANIFEST not in names:
        raise PackError(f"no {MANIFEST} in the runtime zip")
    if len(set(names)) != len(names):
        raise PackError("the runtime zip has a name twice")
    for i in z.infolist():
        kind = stat.S_IFMT(i.external_attr >> 16)
        if kind not in (0, stat.S_IFREG) or i.external_attr & 0x10:
            raise PackError(f"the runtime zip has a special or symlink entry: {i.filename}")
        if i.orig_filename != i.filename:
            raise PackError(f"the runtime zip has a truncated name: {i.orig_filename!r}")
    check_paths(names + [README])
    text = io.BytesIO()
    copy_bounded(z, z.getinfo(MANIFEST), MAX_MANIFEST_BYTES, text)
    m = json.loads(text.getvalue())
    if m.get("format") != 1:
        raise PackError(f"{MANIFEST}: format {m.get('format')!r}, expected 1")
    files = m.get("files") or []
    paths = [f.get("path") for f in files]
    if len(set(paths)) != len(paths):
        raise PackError(f"{MANIFEST}: a path is listed twice")
    for f in files:
        why = unsafe_name(f.get("path") or "")
        if why:
            raise PackError(f"{MANIFEST}: unsafe path {f.get('path')!r}: {why}")
        if f.get("component") not in (m.get("components") or {}):
            raise PackError(f"{MANIFEST}: {f['path']} has unknown component {f.get('component')!r}")
        if f["path"] == README or f["path"].lower() == README.lower():
            raise PackError(f"{MANIFEST}: {f['path']} would collide with the pack's README")
        if type(f.get("size")) is not int or not 0 <= f["size"] <= MAX_FILE_BYTES:
            raise PackError(f"{f['path']}: invalid manifest size")
        if not isinstance(f.get("sha256"), str) or not re.fullmatch(r"[0-9a-f]{64}", f["sha256"]):
            raise PackError(f"{f['path']}: invalid manifest sha256")
    check_paths(paths + [MANIFEST, README])
    extra = sorted(set(names) - set(paths) - {MANIFEST})
    missing = sorted(set(paths) - set(names))
    if extra or missing:
        raise PackError(f"the runtime zip does not match {MANIFEST}: extra {extra[:5]}, missing {missing[:5]}")
    for f in files:
        if z.getinfo(f["path"]).file_size != f["size"]:
            raise PackError(f"{f['path']}: does not match {MANIFEST} (size)")
    return m


def render_readme(template, manifest, version, comps, count):
    builds = [b for b in (manifest.get("builds") or {}).get(SIDE) or [] if b.get("verified", True)]
    build_lines = "\n".join(
        f"  Steam build {b.get('steamBuild', '?')}  (enshrouded_server.exe sha256 {b['sha256'][:16]}...)" for b in builds
    ) or "  (none: Kiln stays idle on every server build until an update adds one)"
    comp_lines = "\n".join(f"  {n:<14} {(manifest['components'][n].get('description') or '').strip()}" for n in comps)
    values = {"VERSION": version, "RULE": "=" * len(f"Kiln server pack {version}"),
              "RUNTIME_VERSION": manifest.get("version") or "?", "COMPONENTS": comp_lines,
              "FILE_COUNT": str(count), "BUILDS": build_lines}
    text = template
    for k, v in values.items():
        text = text.replace(f"@{k}@", v)
    left = sorted(set(re.findall(r"@[A-Z_]+@", text)))
    if left:
        raise PackError(f"README template: unknown placeholders {left}")
    return text.encode("utf-8")


def build(runtime_zip, out, template, version, epoch):
    """Writes the pack; returns {components, files, bytes} (files and bytes without the README)."""
    when = zip_time(epoch)
    with open_zip(runtime_zip) as z:
        m = read_runtime(z)
        comps = server_components(m)
        chosen = sorted((f for f in m["files"] if f["component"] in comps), key=lambda f: f["path"].encode("utf-8"))
        if not any(f["path"] == "dinput8.dll" for f in chosen):
            raise PackError("the server components hold no dinput8.dll")
        readme = render_readme(template, m, version, comps, len(chosen))
        entries = {f["path"]: f for f in chosen}
        entries[README] = None
        parent = os.path.dirname(os.path.abspath(out))
        os.makedirs(parent, exist_ok=True)
        fd, tmp = tempfile.mkstemp(prefix=".kiln-server-", suffix=".tmp", dir=parent)
        try:
            with os.fdopen(fd, "w+b") as raw, zipfile.ZipFile(raw, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zo:
                for name in sorted(entries, key=lambda n: n.encode("utf-8")):
                    info = zipfile.ZipInfo(name, date_time=when)
                    info.create_system = 3
                    info.external_attr = (stat.S_IFREG | 0o644) << 16
                    info.compress_type = zipfile.ZIP_DEFLATED
                    # ZipInfo passed to open() doesn't inherit ZipFile's compression level.
                    info._compresslevel = 6  # also supported before Python 3.13's public compress_level property
                    f = entries[name]
                    if f is None:
                        zo.writestr(info, readme, compresslevel=6)
                    else:
                        with zo.open(info, "w") as dst:
                            size, sha = copy_bounded(z, z.getinfo(name), f["size"], dst)
                        if size != f["size"] or sha != f["sha256"]:
                            raise PackError(f"{name}: does not match {MANIFEST} (size or sha256)")
            os.chmod(tmp, 0o644)
            os.replace(tmp, out)
        finally:
            if os.path.exists(tmp):
                os.remove(tmp)
    return {"components": comps, "files": len(chosen), "bytes": sum(f["size"] for f in chosen)}


def check(pack, runtime_zip, template, version, epoch):
    """Problems with a built pack (empty: fine)."""
    problems = []
    with open_zip(runtime_zip) as z:
        m = read_runtime(z)
    comps = server_components(m)
    want = {f["path"]: f for f in m["files"] if f["component"] in comps}
    with open_zip(pack) as zp:
        infos = zp.infolist()
        names = [i.filename for i in infos]
        if names != sorted(names, key=lambda n: n.encode("utf-8")):
            problems.append("entries are not sorted bytewise")
        if len(set(names)) != len(names):
            problems.append("a name is in the pack twice")
        if len({i.date_time for i in infos}) > 1:
            problems.append("entries have different times")
        for i in infos:
            why = unsafe_name(i.filename)
            if why:
                problems.append(f"{i.filename!r}: unsafe name: {why}")
            if (i.external_attr >> 16) != (stat.S_IFREG | 0o644) or i.create_system != 3 or i.extra:
                problems.append(f"{i.filename}: not a plain 0644 Unix entry")
            if i.filename == README:
                continue
            f = want.get(i.filename)
            if f is None:
                other = next((x["component"] for x in m["files"] if x["path"] == i.filename), None)
                problems.append(f"{i.filename}: not a server file" + (f" (component {other})" if other else ""))
                continue
            size, sha = copy_bounded(zp, i, f["size"])
            if size != f["size"] or sha != f["sha256"]:
                problems.append(f"{i.filename}: differs from {MANIFEST}")
        for p in sorted(set(want) - set(names)):
            problems.append(f"{p}: missing (component {want[p]['component']})")
        if README not in names:
            problems.append(f"no {README}")
    with tempfile.TemporaryDirectory(prefix="kiln-serverpack-", dir=os.path.dirname(os.path.abspath(pack))) as d:
        again = os.path.join(d, os.path.basename(pack))
        build(runtime_zip, again, template, version, epoch)
        if sha256_file(again) != sha256_file(pack):
            problems.append("a second build from the same runtime zip gives other bytes")
    return problems


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    for name in ("build", "check"):
        s = sub.add_parser(name)
        if name == "build":
            s.add_argument("runtime")
            s.add_argument("--out", required=True)
        else:
            s.add_argument("pack")
            s.add_argument("--runtime", required=True)
        s.add_argument("--readme", required=True, help="the KILN-SERVER-README.txt template")
        s.add_argument("--version", required=True, help="the release version (for the README)")
        s.add_argument("--epoch", type=int, default=None, help="entry time (default SOURCE_DATE_EPOCH)")
    a = p.parse_args(argv)
    epoch = a.epoch if a.epoch is not None else int(os.environ.get("SOURCE_DATE_EPOCH") or 0)
    if not epoch:
        p.error("--epoch or SOURCE_DATE_EPOCH is required (the pack must be reproducible)")
    with open(a.readme, encoding="utf-8") as f:
        template = f.read()
    try:
        if a.cmd == "build":
            r = build(a.runtime, a.out, template, a.version, epoch)
            print(f"server pack: {os.path.basename(a.out)}: {r['files']} files ({r['bytes'] / 1048576:.1f} MB unpacked) "
                  f"+ {README}, components {', '.join(r['components'])}, {os.path.getsize(a.out) / 1048576:.1f} MB")
            return 0
        problems = check(a.pack, a.runtime, template, a.version, epoch)
    except (PackError, zipfile.BadZipFile, OSError, ValueError, KeyError) as e:
        print(f"serverpack: {e}", file=sys.stderr)
        return 1
    for x in problems[:100]:
        print(f"serverpack: {x}", file=sys.stderr)
    if problems:
        return 1
    print(f"server pack OK: {a.pack}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
