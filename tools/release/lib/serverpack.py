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
import hashlib
import json
import os
import re
import stat
import sys
import tempfile
import time
import zipfile

README = "KILN-SERVER-README.txt"
MANIFEST = "kiln-runtime.json"
SIDE = "server"
ZIP_EPOCH_MIN = 315532800  # 1980-01-01: the earliest time a zip entry can hold


class PackError(Exception):
    pass


def unsafe_name(name):
    """Why an entry name must not be unzipped into a server folder, or None. Zip names use '/' only."""
    if not name or name.endswith("/"):
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
    if any(p in ("", ".", "..") for p in parts):
        return "an empty, '.' or '..' path part"
    if any(p != p.rstrip(" .") for p in parts):
        return "a part ending in a space or dot (Windows drops it)"
    if any(re.fullmatch(r"(?i)(con|prn|aux|nul|com[0-9]|lpt[0-9])(\..*)?", p) for p in parts):
        return "a reserved Windows device name"
    return None


def server_components(manifest):
    """The components kilnctl deploy --bundle installs on a server: the defaults of the side plus what they require."""
    comps = manifest.get("components") or {}
    want = sorted(n for n, c in comps.items() if c.get("default") and SIDE in (c.get("sides") or []))
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
    m = json.loads(z.read(MANIFEST))
    if m.get("format") != 1:
        raise PackError(f"{MANIFEST}: format {m.get('format')!r}, expected 1")
    files = m.get("files") or []
    paths = [f.get("path") for f in files]
    if len(set(paths)) != len(paths):
        raise PackError(f"{MANIFEST}: a path is listed twice")
    if len(set(names)) != len(names):
        raise PackError("the runtime zip has a name twice")
    for f in files:
        why = unsafe_name(f.get("path") or "")
        if why:
            raise PackError(f"{MANIFEST}: unsafe path {f.get('path')!r}: {why}")
        if f.get("component") not in (m.get("components") or {}):
            raise PackError(f"{MANIFEST}: {f['path']} has unknown component {f.get('component')!r}")
        if f["path"] == README or f["path"].lower() == README.lower():
            raise PackError(f"{MANIFEST}: {f['path']} would collide with the pack's README")
    extra = sorted(set(names) - set(paths) - {MANIFEST})
    missing = sorted(set(paths) - set(names))
    if extra or missing:
        raise PackError(f"the runtime zip does not match {MANIFEST}: extra {extra[:5]}, missing {missing[:5]}")
    for i in z.infolist():
        if stat.S_ISLNK(i.external_attr >> 16):
            raise PackError(f"the runtime zip has a symlink entry: {i.filename}")
    return m


def render_readme(template, manifest, version, comps, count):
    builds = [b for b in (manifest.get("builds") or {}).get(SIDE) or [] if b.get("verified")]
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
    with zipfile.ZipFile(runtime_zip) as z:
        m = read_runtime(z)
        comps = server_components(m)
        chosen = sorted((f for f in m["files"] if f["component"] in comps), key=lambda f: f["path"].encode("utf-8"))
        if not any(f["path"] == "dinput8.dll" for f in chosen):
            raise PackError("the server components hold no dinput8.dll")
        entries = {}
        for f in chosen:
            data = z.read(f["path"])
            if len(data) != f["size"] or hashlib.sha256(data).hexdigest() != f["sha256"]:
                raise PackError(f"{f['path']}: does not match {MANIFEST} (size or sha256)")
            entries[f["path"]] = data
    entries[README] = render_readme(template, m, version, comps, len(chosen))
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    tmp = out + ".tmp"
    try:
        with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zo:
            for name in sorted(entries, key=lambda n: n.encode("utf-8")):
                info = zipfile.ZipInfo(name, date_time=when)
                info.create_system = 3
                info.external_attr = (stat.S_IFREG | 0o644) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                zo.writestr(info, entries[name], compresslevel=6)
        os.replace(tmp, out)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)
    return {"components": comps, "files": len(chosen), "bytes": sum(f["size"] for f in chosen)}


def check(pack, runtime_zip, template, version, epoch):
    """Problems with a built pack (empty: fine)."""
    problems = []
    with zipfile.ZipFile(runtime_zip) as z:
        m = read_runtime(z)
    comps = server_components(m)
    want = {f["path"]: f for f in m["files"] if f["component"] in comps}
    with zipfile.ZipFile(pack) as zp:
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
            data = zp.read(i)
            if len(data) != f["size"] or hashlib.sha256(data).hexdigest() != f["sha256"]:
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
