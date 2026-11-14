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
import contextlib
import hashlib
import json
import os
import re
import shutil
import struct
import stat
import time
from pathlib import Path

from .kinds import KINDS, KindError

FORMAT = 1
TOOL = "kiln-bootsnap 1"
VALUE_MAX = 4 * 1024 * 1024 - 256  # KILN_BOOT_RESULT_MAX less 256 bytes for the result wrapper
INDEX_MAX = 32 * 1024 * 1024
DATA_MAX = 1024 * 1024 * 1024
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


def file_sha256(path, no_links=False):
    h = hashlib.sha256()
    with _read_file(path, no_links) as f:
        size = 0
        for block in iter(lambda: f.read(1 << 20), b""):
            size += len(block)
            if size > DATA_MAX:
                raise ValueError("boot snapshot file exceeds its hashing budget")
            h.update(block)
    return h.hexdigest()


def _read_file(path, no_links=True):
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NONBLOCK", 0)
    if no_links:
        if _linked(path):
            raise ValueError("snapshot files must not be links")
        flags |= getattr(os, "O_NOFOLLOW", 0)
    fd = os.open(path, flags)
    if not stat.S_ISREG(os.fstat(fd).st_mode):
        os.close(fd)
        raise ValueError("snapshot input must be a regular file")
    return os.fdopen(fd, "rb")


def snapshot_key(exe_sha256, version):
    """The first 32 lower-case hex digits of SHA-256 over the exe's SHA-256 as 64 lower-case hex characters, one "\\n",
    then the .kfc's version string (bytes as stored)."""
    if isinstance(version, str):
        version = version.encode("utf-8")
    return hashlib.sha256(exe_sha256.lower().encode("ascii") + b"\n" + version).hexdigest()[:32]


def root_of(game):
    return Path(game) / "kiln" / "data" / "boot-gamedata"


def _linked(path):
    """Includes Windows directory junctions and other reparse points."""
    s = Path(path).lstat()
    return stat.S_ISLNK(s.st_mode) or bool(getattr(s, "st_file_attributes", 0) & 0x400)


@contextlib.contextmanager
def _output_root(game):
    """Open the output tree without following links below the user-selected game folder. On Linux hold directory
    descriptors throughout, so a parent replaced by a symlink during extraction cannot redirect writes or cleanup.
    A second producer refuses immediately rather than renaming or deleting the first one's files."""
    game = Path(game).resolve(strict=True)
    fd = None
    lock = None
    opened = False
    try:
        if os.name == "posix":
            import fcntl
            fd = os.open(game, os.O_RDONLY | os.O_DIRECTORY)
            for part in ("kiln", "data", "boot-gamedata"):
                try:
                    os.mkdir(part, dir_fd=fd)
                except FileExistsError:
                    pass
                child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                os.close(fd)
                fd = child
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            opened = True
            yield Path(f"/proc/self/fd/{fd}")
        else:
            root = game
            for part in ("kiln", "data", "boot-gamedata"):
                root /= part
                root.mkdir(exist_ok=True)
                if _linked(root) or not root.is_dir():
                    raise SnapshotError(f"{root}: snapshot output must be a directory without links")
            # Keep a persistent lock: unlinking it on close would allow a third writer to bypass a waiter.
            import msvcrt
            lock_path = root / ".producer.lock"
            if lock_path.is_symlink() or (lock_path.exists() and _linked(lock_path)):
                raise SnapshotError("the snapshot producer lock must not be a link")
            lock = open(root / ".producer.lock", "a+b")
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            opened = True
            yield root
    except OSError as e:
        if opened:
            raise
        raise SnapshotError(f"cannot safely open or lock the boot snapshot directory: {e}") from e
    finally:
        if lock is not None:
            lock.close()
        if fd is not None:
            os.close(fd)


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


NOT_NEEDED = "no mod needs the boot snapshot"


def _jsonc(text):
    """Strip JSONC comments/trailing commas outside strings; URLs and text such as `,}` are literal values."""
    quoted = r'"(?:\\.|[^"\\])*"'
    text = re.sub(f'({quoted})|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/',
                  lambda m: m[1] if m[1] else " ", text)
    text = re.sub(f'({quoted})|,(\\s*[}}\\]])', lambda m: m[1] if m[1] else m[2], text)
    return json.loads(text)


def _read_mod_json(path):
    with open(path, "rb") as f:
        raw = f.read((16 << 20) + 1)
    if len(raw) > 16 << 20:
        raise ValueError("boot manifest or profile exceeds 16 MiB")
    return _jsonc(raw.decode("utf-8-sig"))


def boot_mods(game, server):
    """Enabled profile entries, then allowed/unlisted drop-ins, as kiln.boot scans them. Without a profile all
    non-disabled drop-ins count. A server leaves out client-only mods; other loaders never need this snapshot."""
    kiln = Path(game) / "kiln"
    root = kiln / "mods"
    out, listed = [], set()

    def boot_id(d):
        try:
            m = _read_mod_json(d / "kiln.mod.json")
        except (OSError, ValueError):
            return None
        boot = m.get("boot") if isinstance(m, dict) else None
        if (boot is True or boot == "required") and not (server and m.get("side", "server") == "client"):
            mid = m.get("id")
            return mid if isinstance(mid, str) and re.fullmatch(r"[a-z0-9][a-z0-9._-]{0,63}", mid) else None
        return None

    dropins = True
    profile = kiln / "profile.json"
    if profile.exists():
        try:
            p = _read_mod_json(profile)
            if p.get("format") != 1 or not isinstance(p.get("mods"), dict):
                return []
            entries = p["mods"].get("kiln", [])
            if not isinstance(entries, list) or len(entries) > 4096:
                return []
            drop = p.get("dropins", False)
            dropins = drop is True or isinstance(drop, list) and "kiln" in drop
            for e in entries:
                listed.add(e["id"])
                if e.get("enabled", True) is not True:
                    continue
                path = e["path"].replace("\\", "/")
                if os.name != "nt" and path[:3].lower() == "z:/":
                    path = path[2:]
                elif os.name != "nt" and re.match(r"^[a-zA-Z]:/", path):
                    raise SnapshotError("the boot profile uses a Windows drive not mapped on this host; run kiln boot-snapshot without --if-needed")
                d = Path(path)
                if not d.is_absolute():
                    d = kiln / d
                if boot_id(d) == e["id"] and e["id"] not in out:
                    out.append(e["id"])
        except (OSError, ValueError, TypeError, KeyError, AttributeError):
            return []
    if dropins:
        for d in sorted(root.iterdir()) if root.is_dir() else []:
            if not d.is_dir() or (d / ".disabled").exists():
                continue
            mid = boot_id(d)
            if mid and mid not in listed and mid not in out:
                out.append(mid)
    return out


def empty_kinds(skipped):
    """The kinds written empty because the archive could not give them (their `skipped` line has no name)."""
    return [s["kind"] for s in skipped if not s.get("name") and str(s.get("reason", "")).startswith("kind not read")]


# ---- reading a snapshot back (reuse, `show`) ------------------------------------------------------------------------
def valid(path, key):
    """Verify all eight files before reuse. The marker's producer-only checksums are ignored by the format-1 reader;
    older snapshots without them are rebuilt once. Mere file presence cannot detect a truncated or damaged cache."""
    p = Path(path)
    try:
        if _linked(p):
            return False
        idx = _read_index(p / "index.json")
        if not isinstance(idx, dict) or type(idx.get("format")) is not int or idx["format"] != FORMAT or idx.get("key") != key or p.name != key:
            return False
        if not isinstance(idx.get("skipped"), list) or any(not isinstance(s, dict) or
                any(not isinstance(s.get(k), str) for k in ("kind", "name", "reason")) for s in idx["skipped"]):
            return False
        checksums = idx.get("checksums", {})
        for kind in KINDS:
            for suffix, limit in (("index.json", INDEX_MAX), ("data", DATA_MAX)):
                file = p / f"{kind}.{suffix}"
                if _linked(file) or not file.is_file() or file.stat().st_size > limit or checksums.get(file.name) != file_sha256(file, no_links=True):
                    return False
            ix = _read_index(p / f"{kind}.index.json")
            if type(ix.get("format")) is not int or ix["format"] != FORMAT or ix.get("kind") != kind or not isinstance(ix.get("entries"), dict):
                return False
            if idx.get("counts", {}).get(kind) != len(ix["entries"]):
                return False
            at = 0
            for e in sorted(ix["entries"].values(), key=lambda e: e[0]):
                if (not isinstance(e, list) or len(e) != 5 or type(e[0]) is not int or type(e[1]) is not int
                        or e[0] != at or not 0 < e[1] <= VALUE_MAX
                        or not isinstance(e[2], str) or not re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", e[2])
                        or not isinstance(e[3], str) or not re.fullmatch(r"[0-9a-f]{8}", e[3])
                        or type(e[4]) is not int or not 0 <= e[4] <= 0x7fffffff):
                    return False
                at += e[1]
            if at != (p / f"{kind}.data").stat().st_size:
                return False
        return True
    except (OSError, ValueError, TypeError, KeyError, IndexError, AttributeError, RecursionError):
        return False


def _read_index(path):
    with _read_file(path) as f:
        raw = f.read(INDEX_MAX + 1)
    if len(raw) > INDEX_MAX:
        raise ValueError("snapshot index exceeds 32 MiB")
    return json.loads(raw)


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
        if _linked(d) or not d.is_dir() or d.name == keep:
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
    with open(path, "xb") as f:
        size = 0
        for part in json.JSONEncoder(ensure_ascii=False, separators=(",", ":"), allow_nan=False).iterencode(obj):
            raw = part.encode("utf-8")
            size += len(raw)
            if size > INDEX_MAX:
                raise SnapshotError("snapshot index exceeds its 32 MiB budget")
            f.write(raw)
        f.flush()
        os.fsync(f.fileno())


def _sync_dir(path):
    if os.name == "posix":
        fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)


def write_kind(src, kind, out_dir, skipped, progress=None):
    """<kind>.data and <kind>.index.json; returns the count stored. A kind the archive cannot give is written empty with
    one `skipped` line saying why."""
    kept, order = {}, []
    try:
        rows = []
        for r in src.rows(kind):
            if len(rows) >= 100000:
                raise SnapshotError(f"{kind}: snapshot exceeds its entry budget")
            rows.append(r)
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
    with open(out_dir / f"{kind}.data", "xb") as data:
        at = 0
        for i, r in enumerate(sorted(kept.values(), key=lambda r: (r.sort, r.order))):  # stream order: each chunk once
            try:
                value = r.value()
                if not isinstance(value, dict):
                    raise ValueError("the resource is not a JSON object")
                raw = json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
            except TimeoutError:
                raise
            except Exception as e:  # a blob the reflection does not fit: one entry lost, never the snapshot
                skipped.append({"kind": kind, "name": r.name, "reason": f"decode failed: {e}"})
                continue
            if len(raw) > VALUE_MAX:
                skipped.append({"kind": kind, "name": r.name, "reason": "over 4 MiB"})
                continue
            if at + len(raw) > DATA_MAX:
                raise SnapshotError(f"{kind}: snapshot exceeds its 1 GiB data budget")
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
    exe = find_exe(game, exe)
    display_root = root_of(Path(game).resolve(strict=True))
    if root is not None and Path(root).absolute() != display_root:
        raise SnapshotError("--root must be <game>/kiln/data/boot-gamedata")
    with _output_root(game) as safe_root:
        r = _make(game, src_factory, exe, force, safe_root, progress, after_kind)
        try:
            same_root = os.path.samestat(display_root.stat(), safe_root.stat())
        except OSError:
            same_root = False
        if not same_root:
            raise SnapshotError("the snapshot output directory changed during extraction")
    r["path"] = str(display_root / r["key"])
    return r


def _make(game, src_factory, exe, force, root, progress, after_kind):
    """Makes (or reuses) the snapshot of the game in `game`. `src_factory(exe)` builds the kinds.Source once a build is
    needed (it reads archive_files(exe)). Returns a summary dict: status "reused" | "made", key, path, emptyKinds,
    seconds; a made one also counts, skipped, archive (which files were read) and bytes (its size on disk). Snapshots
    of other keys are removed only after this key's is in place and reads back."""
    t0 = time.time()
    exe = find_exe(game, exe)
    plain = exe.with_suffix(".kfc")
    if not plain.is_file():
        raise SnapshotError(f"no {plain.name} beside {exe.name}")
    if exe.stat().st_size > 256 << 20:
        raise SnapshotError("boot executable exceeds its 256 MiB budget")
    version = kfc_version(plain)  # kiln.boot reads the live header: the key follows it
    exe_sha = file_sha256(exe)
    key = snapshot_key(exe_sha, version)
    final = root / key
    for p in (final, root / f"{key}.tmp-{os.getpid()}", root / f"{key}.old-{os.getpid()}"):
        if p.is_symlink() or (p.exists() and _linked(p)):
            raise SnapshotError(f"{p.name}: snapshot output must not be a link")
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
            "checksums": {f"{k}.{s}": file_sha256(tmp / f"{k}.{s}")
                          for k in KINDS for s in ("index.json", "data")},
        })
        _sync_dir(tmp)
        old = None
        if final.exists():
            old = root / f"{key}.old-{os.getpid()}"
            os.rename(final, old)
        try:
            os.rename(tmp, final)
            _sync_dir(root)
        except OSError:
            if valid(final, key):  # another producer finished the same key first: its snapshot stands
                shutil.rmtree(tmp, ignore_errors=True)
                return {"status": "reused", "key": key, "path": str(final), "emptyKinds": _empty_kinds_of(final),
                        "seconds": round(time.time() - t0, 3)}
            if old is not None and not final.exists():
                os.rename(old, final)
            raise
        if not valid(final, key):
            shutil.rmtree(final, ignore_errors=True)
            if old is not None:
                os.rename(old, final)
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
