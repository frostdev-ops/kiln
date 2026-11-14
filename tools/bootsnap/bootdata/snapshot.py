# SPDX-License-Identifier: MIT
"""The boot game-data snapshot on disk, format 1 (contract c-08, docs/kiln/modding/boot-gamedata.md): the key, the
files, the finished-marker, reuse and replace.

    <game>/kiln/data/boot-gamedata/<key>/
        index.json                    written last: its presence is the finished-marker
        <kind>.index.json, <kind>.data    for items, templates, skills, materials

The producer writes into `<key>.tmp-<pid>/` and renames the directory, so a directory named by a key is complete or
absent; a run that is killed leaves only its `.tmp-<pid>` directory, without `index.json`, which the next run removes.
"""
import datetime
import hashlib
import json
import os
import shutil
import struct
import time
from pathlib import Path

from .kinds import KINDS, KindError

FORMAT = 1
TOOL = "kiln-bootsnap 1"
VALUE_MAX = 4 * 1024 * 1024 - 256  # KILN_BOOT_RESULT_MAX less 256 bytes for the result wrapper
STALE_TMP_SECONDS = 3600            # a .tmp directory of a process that may still run is left alone this long


class SnapshotError(Exception):
    pass


# ---- the game folder ----------------------------------------------------------------------------------------------
def find_exe(game, exe=None):
    """The game executable of a folder (read as a file only, never run): the dedicated server's, else the client's."""
    if exe:
        p = Path(exe)
        if not p.is_file():
            raise SnapshotError(f"no file {p}")
        return p
    g = Path(game)
    for name in ("enshrouded_server.exe", "enshrouded.exe"):
        if (g / name).is_file():
            return g / name
    raise SnapshotError(f"{g}: neither enshrouded_server.exe nor enshrouded.exe is there")


def side_of(exe):
    return "server" if Path(exe).stem.lower().endswith("_server") else "client"


def kfc_version(kfc_path):
    """The version string of a .kfc: header 0x10 {u32 rel, u32 len}, the bytes at 0x10 + rel (one 64 KiB read)."""
    with open(kfc_path, "rb") as f:
        head = f.read(65536)
    if head[:4] != b"KFC4" or len(head) < 0x18:
        raise SnapshotError(f"{kfc_path} is not a KFC4 archive")
    rel, n = struct.unpack_from("<II", head, 0x10)
    at = 0x10 + rel
    if n == 0 or at + n > len(head):
        raise SnapshotError(f"{kfc_path}: the version string is not in its first 64 KiB")
    return head[at:at + n]


def file_sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def snapshot_key(exe_sha256, version):
    """The first 32 lower-case hex digits of SHA-256 over the exe's SHA-256 as 64 lower-case hex characters, one "\\n",
    then the .kfc's version string (bytes as stored)."""
    if isinstance(version, str):
        version = version.encode("utf-8")
    return hashlib.sha256(exe_sha256.lower().encode("ascii") + b"\n" + version).hexdigest()[:32]


def root_of(game):
    return Path(game) / "kiln" / "data" / "boot-gamedata"


def archive_files(exe):
    """The files that hold the game's data as it shipped: (index, resources, which).

    EML (gpl/kiln-eml) backs the pair up before its first patch (`create_backup`: `.kfc_resources.bak`, then `.kfc.bak`,
    then a seal) and patches only after that, so a patched pair always has a backup beside it. Older EML kept the
    header alone (`.kfc.bak` and the live `.kfc_resources`, whose prefix it checked). After a game update Steam puts
    the new vanilla pair in place while the backup is still of the old build (EML replaces it at its next start): a
    backup whose version string is not the live one is not the game's data any more, and the live pair is."""
    plain = exe.with_suffix(".kfc")
    res = Path(str(exe.with_suffix("")) + ".kfc_resources")
    bak, bak_res = Path(str(plain) + ".bak"), Path(str(res) + ".bak")
    if bak.is_file():
        try:
            same = kfc_version(bak) == kfc_version(plain)
        except (OSError, SnapshotError):
            same = False
        if same:
            return (bak, bak_res, "backup") if bak_res.is_file() else (bak, res, "backup header")
        return plain, res, "plain (the backup is of another game build)"
    return plain, res, "plain"


def empty_kinds(skipped):
    """The kinds written empty because the archive could not give them (their `skipped` line has no name)."""
    return [s["kind"] for s in skipped if not s.get("name") and str(s.get("reason", "")).startswith("kind not read")]


# ---- reading a snapshot back (reuse, `show`) ------------------------------------------------------------------------
def valid(path, key):
    """A snapshot kiln.boot would read: index.json readable, format 1, its key the directory's and `key`, all eight kind
    files present."""
    p = Path(path)
    try:
        idx = json.loads((p / "index.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return False
    if not isinstance(idx, dict) or idx.get("format") != FORMAT or idx.get("key") != key or p.name != key:
        return False
    return all((p / f"{k}.index.json").is_file() and (p / f"{k}.data").is_file() for k in KINDS)


def read_value(path, kind, name):
    """(entry, value) of one entry of a snapshot, or None."""
    p = Path(path)
    idx = json.loads((p / f"{kind}.index.json").read_text(encoding="utf-8"))
    e = idx["entries"].get(name)
    if e is None:
        return None
    with open(p / f"{kind}.data", "rb") as f:
        f.seek(e[0])
        raw = f.read(e[1])
    return e, json.loads(raw.decode("utf-8"))


# ---- writing ----------------------------------------------------------------------------------------------------------
def _pid_alive(pid):
    if os.name != "posix":
        return None  # unknown: judged by age only
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except OSError:
        return True
    return True


def tidy(root, keep):
    """Removes what the producer owns under boot-gamedata/ and does not need: snapshots of other keys, `.old-` leftovers,
    `.tmp-` directories of runs that are gone (killed: their process is dead, or the directory is an hour old)."""
    removed = []
    if not root.is_dir():
        return removed
    now = time.time()
    for d in root.iterdir():
        if not d.is_dir() or d.name == keep:
            continue
        name = d.name
        stale = False
        if ".tmp-" in name:
            try:
                pid = int(name.rsplit(".tmp-", 1)[1])
            except ValueError:
                pid = -1
            if pid == os.getpid():
                continue
            alive = _pid_alive(pid) if pid > 0 else False
            stale = alive is False or now - d.stat().st_mtime > STALE_TMP_SECONDS
        elif ".old-" in name or (len(name) == 32 and all(c in "0123456789abcdef" for c in name)):
            stale = True
        if stale:
            shutil.rmtree(d, ignore_errors=True)
            removed.append(name)
    return removed


def _write_json(path, obj):
    data = json.dumps(obj, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
    with open(path, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())


def write_kind(src, kind, out_dir, skipped, progress=None):
    """<kind>.data and <kind>.index.json; returns the count stored. A kind the archive cannot give is written empty with
    one `skipped` line saying why."""
    kept, order = {}, []
    try:
        rows = list(src.rows(kind))
    except KindError as e:
        rows = []
        skipped.append({"kind": kind, "name": "", "reason": f"kind not read: {e}"})
    for r in rows:  # resource order: the first of two equal names is stored
        if not r.name:
            skipped.append({"kind": kind, "name": "", "reason": f"no name (resource {r.key.guid})"})
        elif r.name in kept:
            skipped.append({"kind": kind, "name": r.name, "reason": "duplicate name"})
        else:
            kept[r.name] = r
            order.append(r.name)
    entries = {}
    with open(out_dir / f"{kind}.data", "wb") as data:
        at = 0
        for i, r in enumerate(sorted(kept.values(), key=lambda r: (r.sort, r.order))):  # stream order: each chunk once
            try:
                value = r.value()
                raw = json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
            except Exception as e:  # a blob the reflection does not fit: one entry lost, never the snapshot
                skipped.append({"kind": kind, "name": r.name, "reason": f"decode failed: {e}"})
                continue
            if len(raw) > VALUE_MAX:
                skipped.append({"kind": kind, "name": r.name, "reason": "over 4 MiB"})
                continue
            data.write(raw)
            entries[r.name] = [at, len(raw), r.key.guid, f"{r.key[1]:08x}", r.key[2]]
            at += len(raw)
            if progress and i % 500 == 499:
                progress(kind, i + 1, len(kept))
        data.flush()
        os.fsync(data.fileno())
    _write_json(out_dir / f"{kind}.index.json",
                {"format": FORMAT, "kind": kind, "entries": {n: entries[n] for n in order if n in entries}})
    return len(entries)


def make(game, src_factory, exe=None, force=False, root=None, progress=None, after_kind=None):
    """Makes (or reuses) the snapshot of the game in `game`. `src_factory(exe)` builds the kinds.Source once a build is
    needed (it reads archive_files(exe)). Returns a summary dict: status "reused" | "made", key, path, emptyKinds,
    seconds; a made one also counts, skipped, archive (which files were read) and bytes (its size on disk). Snapshots
    of other keys are removed only after this key's is in place and reads back."""
    t0 = time.time()
    exe = find_exe(game, exe)
    plain = exe.with_suffix(".kfc")
    if not plain.is_file():
        raise SnapshotError(f"no {plain.name} beside {exe.name}")
    version = kfc_version(plain)  # kiln.boot reads the live header: the key follows it
    exe_sha = file_sha256(exe)
    key = snapshot_key(exe_sha, version)
    root = Path(root) if root else root_of(game)
    final = root / key
    if not force and valid(final, key):
        tidy(root, key)  # older keys go only once this one is known good
        return {"status": "reused", "key": key, "path": str(final), "emptyKinds": _empty_kinds_of(final),
                "seconds": round(time.time() - t0, 3)}
    root.mkdir(parents=True, exist_ok=True)
    tmp = root / f"{key}.tmp-{os.getpid()}"
    if tmp.exists():
        shutil.rmtree(tmp)
    tmp.mkdir()
    src = None
    try:
        src = src_factory(exe)
        skipped, counts = [], {}
        for kind in KINDS:
            counts[kind] = write_kind(src, kind, tmp, skipped, progress)
            if after_kind:
                after_kind(kind, tmp)
        src.close()  # the archive's files are not held open while the directory is renamed
        src = None
        text = version.decode("utf-8", "replace")
        _write_json(tmp / "index.json", {
            "format": FORMAT, "key": key, "side": side_of(exe), "exeSha256": exe_sha, "kfcVersion": text,
            "gameBuild": text.split("|", 1)[0], "tool": TOOL,
            "written": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "counts": counts, "skipped": skipped, "runtimeOnly": [],
        })
        old = None
        if final.exists():
            old = root / f"{key}.old-{os.getpid()}"
            os.rename(final, old)
        try:
            os.rename(tmp, final)
        except OSError:
            if valid(final, key):  # another producer finished the same key first: its snapshot stands
                shutil.rmtree(tmp, ignore_errors=True)
                return {"status": "reused", "key": key, "path": str(final), "emptyKinds": _empty_kinds_of(final),
                        "seconds": round(time.time() - t0, 3)}
            raise
        if not valid(final, key):
            shutil.rmtree(final, ignore_errors=True)
            raise SnapshotError(f"{final}: the snapshot just written does not read back; older snapshots are kept")
        if old is not None:
            shutil.rmtree(old, ignore_errors=True)
    except BaseException:
        if src is not None:
            src.close()
        shutil.rmtree(tmp, ignore_errors=True)
        raise
    tidy(root, key)  # written, renamed and read back: now the older keys go
    return {"status": "made", "key": key, "path": str(final), "counts": counts, "skipped": len(skipped),
            "emptyKinds": empty_kinds(skipped), "archive": archive_files(exe)[2], "bytes": _size_of(final),
            "seconds": round(time.time() - t0, 3)}


def _empty_kinds_of(path):
    try:
        return empty_kinds(json.loads((Path(path) / "index.json").read_text(encoding="utf-8")).get("skipped", []))
    except (OSError, ValueError):
        return []


def _size_of(path):
    return sum(p.stat().st_size for p in Path(path).iterdir() if p.is_file())
