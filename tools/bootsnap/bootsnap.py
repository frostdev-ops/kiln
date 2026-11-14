#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""kiln-bootsnap: the game-data snapshot Mod.OnBoot reads (contract c-08, docs/kiln/modding/boot-gamedata.md).

    bootsnap.py make --game <dir> [--force] [--if-needed] [--exe <file>] [--root <dir>] [--json]
    bootsnap.py show --game <dir> [--root <dir>] <query>...      item:<name> template:<name> skill:0x<hex> material:<name>
    bootsnap.py key  --game <dir> [--exe <file>]

`make` reads the game's own archive (snapshot.archive_files: the backup another loader kept aside, else the live pair) and the game
executable's reflection, both as files (the executable is never run), and writes
<game>/kiln/data/boot-gamedata/<key>/ unless a snapshot of that key is there already. Reuse costs one SHA-256 of the
executable, one 64 KiB read and checksum verification of the snapshot. --json prints one JSON line with the result
(kiln-manage reads it); progress goes to stderr. --if-needed (the deploy step) makes it only when an enabled profile
mod or an allowed drop-in asks for the boot phase, else says
"no mod needs the boot snapshot" (status "skipped") and touches nothing. Exit 0 made, reused or skipped, 1 refused
(the message says why), 2 bad arguments.
"""
import argparse
import io
import json
import struct
import sys
import time
from collections import OrderedDict
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "kfc"))

from bootdata import kinds, live, snapshot  # noqa: E402
from kfc import refl  # noqa: E402
from kfc import archive  # noqa: E402
from kfc.pe import PeImage  # noqa: E402

QUERY_KINDS = {"item": "items", "template": "templates", "skill": "skills", "material": "materials"}
EXE_MAX = 256 << 20
ARCHIVE_MAX = 16 << 20
CHUNK_MAX = 8 << 20
BLOB_MAX = 16 << 20


class Kfc(archive.Kfc):
    """Boot-specific limits, before the shared archive reader allocates tables, resources or zstd output."""

    def __init__(self, path, stem, resources):
        self.deadline = time.monotonic() + 600
        self.path, self.stem, self._resources = Path(path), Path(stem), Path(resources)
        with open(path, "rb") as f:
            self.b = f.read(ARCHIVE_MAX + 1)
        if not 0x90 <= len(self.b) <= ARCHIVE_MAX or self.b[:4] != b"KFC4":
            raise ValueError("boot archive header exceeds its budget or is not KFC4")
        # Validate every array span before the shared parser builds Python lists/dictionaries.
        strides = (1, 16, 1, 1, 20, 4, 8, 16, 16, 8, 32, 8, 8, 4, 12, 20)
        for at, stride in zip(range(0x10, 0x90, 8), strides):
            rel, n = struct.unpack_from("<II", self.b, at)
            if n > 250000 or at + rel + n * stride > len(self.b):
                raise ValueError("boot archive table exceeds its budget or file bounds")
        for a, b in ((0x48, 0x50), (0x60, 0x68), (0x78, 0x80)):
            if self._loc(a)[1] != self._loc(b)[1]:
                raise ValueError("boot archive key/value counts differ")
        self._parse()
        self._res_file, self._chunks_cache, self._chunk_cache_max, self._dat_files = None, OrderedDict(), 6, {}
        end, packed_end = 0, self._resources.stat().st_size
        for off, stored, packed, raw, size in self.chunks:
            if not 0 < size <= CHUNK_MAX or not 0 < packed <= (16 << 20) or packed > stored or off + packed > packed_end or raw != end:
                raise ValueError("boot archive chunk exceeds its budget or stream bounds")
            end += size
        if end != self.stream.get("raw_size", 0):
            raise ValueError("boot archive chunks do not cover the resource stream")
        if any(off + size > end for off, size in self.values):
            raise ValueError("boot resource is outside the stream bounds")
        if any(i >= len(self.keys) for i in self.order) or any(b["first"] + b["count"] > len(self.order) for b in self.bundles.values()):
            raise ValueError("boot archive bundle is outside the resource order")

    def read_stream(self, offset, size):
        if size > BLOB_MAX:
            raise ValueError("boot resource exceeds its 16 MiB budget")
        return super().read_stream(offset, size)

    def _chunk(self, i):
        if time.monotonic() >= self.deadline:
            raise TimeoutError("boot snapshot exceeded its ten-minute decoding budget")
        if i in self._chunks_cache:
            self._chunks_cache.move_to_end(i)
            return self._chunks_cache[i]
        if self._res_file is None:
            self._res_file = open(self._resources, "rb")
        off, _stored, packed, _raw, size = self.chunks[i]
        self._res_file.seek(off)
        data = self._res_file.read(packed)
        if len(data) != packed:
            raise ValueError("short boot archive chunk")
        if archive._zstd is not None:
            z = archive._zstd.ZstdDecompressor(options={archive._zstd.DecompressionParameter.window_log_max: 23})
            out = z.decompress(data, max_length=size + 1)
            if not z.eof or z.unused_data:
                raise ValueError("boot archive zstd frame exceeds its output budget or is incomplete")
        elif archive._zstandard is not None:  # pragma: no cover: older Python with the optional package
            with archive._zstandard.ZstdDecompressor(max_window_size=8192).stream_reader(io.BytesIO(data), read_across_frames=True) as z:
                out = z.read(size + 1)
        else:  # system libzstd: use a fixed destination, never the frame's claimed output size
            import ctypes
            lib = archive._system_libzstd()
            if not lib:
                raise ValueError("no zstd decoder for the boot snapshot")
            buf = ctypes.create_string_buffer(size + 1)
            n = lib.ZSTD_decompress(buf, size + 1, data, len(data))
            if lib.ZSTD_isError(n):
                raise ValueError("boot archive zstd frame exceeds its output budget or is damaged")
            out = buf.raw[:n]
        if len(out) != size:
            raise ValueError("boot archive zstd output does not match the chunk size")
        self._chunks_cache[i] = out
        if len(self._chunks_cache) > self._chunk_cache_max:
            self._chunks_cache.popitem(last=False)
        return out


def _check_reflection(exe):
    """Check PE allocation sizes and aggregate reflection counts before the shared extractor materializes them."""
    with open(exe, "rb") as f:
        raw = f.read(EXE_MAX + 1)
    if len(raw) > EXE_MAX:
        raise ValueError("boot executable exceeds its 256 MiB budget")
    try:
        pe = struct.unpack_from("<I", raw, 0x3c)[0]
        n, opt = struct.unpack_from("<H", raw, pe + 6)[0], struct.unpack_from("<H", raw, pe + 20)[0]
        if not 0 < n <= 96:
            raise ValueError("boot PE section count exceeds its budget")
        for i in range(n):
            at = pe + 24 + opt + 40 * i
            vsz, va, rsz, off = struct.unpack_from("<4I", raw, at + 8)
            if va + max(vsz, rsz) > EXE_MAX or off + rsz > len(raw):
                raise ValueError("boot PE section exceeds its image budget or file bounds")
    except struct.error as e:
        raise ValueError("truncated boot executable") from e
    del raw
    image = PeImage(exe)
    records = refl.scan(image)
    fields = 0
    if len(records) > 100000:
        raise ValueError("boot reflection exceeds its type budget")
    for r in records:
        kind, count = image.d(r + refl.REC_KIND) & 0xff, image.d(r + refl.REC_COUNT)
        if kind == refl.K_STRUCT or kind == refl.K_ENUM:
            fields += count
        if fields > 1000000:
            raise ValueError("boot reflection exceeds its field budget")


def reflection(exe):
    """The executable's reflection (refl.extract, about 2 s). A dump tools/kfc cached for this exact executable under
    $KILN_OUT/types/<sha16>.json is read instead when there is one (developers and tests, as the cook tools do); this
    tool never writes one."""
    import os
    out = os.environ.get("KILN_OUT")
    if out:
        cached = Path(out) / "types" / f"{snapshot.file_sha256(exe)[:16]}.json"
        if cached.is_file():
            with open(cached, "rb") as f:
                raw = f.read((64 << 20) + 1)
            if len(raw) > 64 << 20:
                raise ValueError("cached boot reflection exceeds its budget")
            return json.loads(raw)
    _check_reflection(exe)
    return refl.extract(str(exe))


def open_source(exe, extract=None):
    """The archive as the game shipped it (the `.bak` pair when another loader patched it), the type graph of `exe` (its
    reflection, read from the file), the config vocabulary of its impact programs. `extract`: tests give a type dump."""
    index, resources, _which = snapshot.archive_files(exe)
    kfc = Kfc(index, stem=exe.with_suffix(""), resources=resources)
    try:
        graph = live.Graph((extract or reflection)(str(exe)))
        configs = live.ConfigNames()
        for key in kfc.resources_of_type(kinds.fnv1a32("keen::ImpactProgram")):
            configs.add_program(graph, kfc.read(key))
        configs.finish()
        return kinds.Source(kfc, graph, live.Walker(graph, configs, deadline=kfc.deadline))
    except BaseException:
        kfc.close()
        raise


def cmd_make(a):
    def progress(kind, done, total):
        print(f"boot-snapshot: {kind} {done}/{total}", file=sys.stderr, flush=True)

    if a.if_needed:
        exe = snapshot.find_exe(a.game, a.exe)
        mods = snapshot.boot_mods(a.game, snapshot.side_of(exe) == "server")
        if not mods:  # nothing runs and nothing is touched (a snapshot that is there stays)
            r = {"status": "skipped", "reason": snapshot.NOT_NEEDED, "emptyKinds": []}
            print(json.dumps(r) if a.json else f"boot-snapshot: {snapshot.NOT_NEEDED}")
            return 0
        print(f"boot-snapshot: needed by {', '.join(mods)}", file=sys.stderr, flush=True)
    print("boot-snapshot: checking the game data snapshot", file=sys.stderr, flush=True)
    r = snapshot.make(a.game, open_source, exe=a.exe, force=a.force, root=a.root, progress=progress)
    for kind in r.get("emptyKinds", []):  # a snapshot without this kind: every read of it in OnBoot answers "not found"
        print(f"boot-snapshot: warning: the game's {kind} could not be read: the snapshot holds none, and an OnBoot "
              f"read of {kind[:-1]}: finds nothing", file=sys.stderr, flush=True)
    if a.json:
        print(json.dumps(r))
    elif r["status"] == "reused":
        print(f"boot-snapshot: {r['path']} is current ({r['seconds']} s)")
    else:
        c = r["counts"]
        print(f"boot-snapshot: wrote {r['path']} ({r['bytes'] / 1e6:.0f} MB, from the {r['archive']} archive) in {r['seconds']} s: "
              f"{c['items']} items, {c['templates']} templates, {c['skills']} skills, {c['materials']} materials ({r['skipped']} skipped)")
    return 0


def cmd_key(a):
    exe = snapshot.find_exe(a.game, a.exe)
    print(snapshot.snapshot_key(snapshot.file_sha256(exe), snapshot.kfc_version(exe.with_suffix(".kfc"))))
    return 0


def cmd_show(a):
    exe = snapshot.find_exe(a.game, a.exe)
    key = snapshot.snapshot_key(snapshot.file_sha256(exe), snapshot.kfc_version(exe.with_suffix(".kfc")))
    path = (Path(a.root) if a.root else snapshot.root_of(a.game)) / key
    if not snapshot.valid(path, key):
        print(f"boot-snapshot: no snapshot {path}", file=sys.stderr)
        return 1
    rc = 0
    for q in a.query:
        prefix, _, name = q.partition(":")
        kind = QUERY_KINDS.get(prefix)
        if kind is None or not name:
            print(f"boot-snapshot: {q}: not item:/template:/skill:/material:", file=sys.stderr)
            return 2
        if kind == "skills":
            name = name.lower()
        got = snapshot.read_value(path, kind, name)
        if got is None:
            print(json.dumps({"query": q, "found": False}))
            rc = 1
            continue
        e, value = got
        print(json.dumps({"query": q, "resourceId": e[2], "typeHash": e[3], "part": e[4], "value": value}, indent=1))
    return rc


def main(argv=None):
    p = argparse.ArgumentParser(prog="bootsnap", description=__doc__.split("\n\n")[0])
    sub = p.add_subparsers(dest="cmd", required=True)
    for name in ("make", "show", "key"):
        s = sub.add_parser(name)
        s.add_argument("--game", required=True, help="the game folder (the dedicated server's or the client's)")
        s.add_argument("--exe", help="the executable, when the folder holds another name")
        if name != "key":
            s.add_argument("--root", help="snapshot directory (make requires <game>/kiln/data/boot-gamedata)")
        if name == "make":
            s.add_argument("--force", action="store_true", help="rebuild even when the key matches")
            s.add_argument("--json", action="store_true", help="one JSON line with the result on stdout")
            s.add_argument("--if-needed", action="store_true",
                           help="only when an enabled profile mod or allowed drop-in asks for the boot phase (\"boot\": true or \"required\")")
        if name == "show":
            s.add_argument("query", nargs="+")
    a = p.parse_args(argv)
    try:
        return {"make": cmd_make, "show": cmd_show, "key": cmd_key}[a.cmd](a)
    except (snapshot.SnapshotError, OSError, ValueError) as e:
        if getattr(a, "json", False):
            print(json.dumps({"status": "refused", "reason": str(e)}))
        print(f"boot-snapshot: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
