# SPDX-License-Identifier: MIT
"""kfc.py terrain ...: the baked terrain's tiles (kfc/voxeltile.py, kfc/voxelworld.py, kfc/terraincook.py).

    kfc.py [--game DIR] terrain info [--part P]
    kfc.py [--game DIR] terrain check [--part 0|1|all] [--level L|all] [--tiles I,J|--sample N|--all] [--no-decode]
                                      [--canonical] [--hash] [--jobs N] [-o REPORT.json]
    kfc.py [--game DIR] terrain column X Z [--part P] [--level L] [--cells]
    kfc.py [--game DIR] terrain cook PLAN.json -o DIR

info: the root of each part: size, levels, tile grids, referenced bytes, the companions' sizes, the layout check.
check: every rule the reader relies on, per tile (envelope; every column's runs, control bits and payload; with decode
  every compressed block; the tile's dimension bytes against the root's grid), plus a byte-identical structural
  rebuild; --canonical: Kiln's writer must rebuild every column byte for byte from its cells and the game's choice of
  which one-value blocks it stores compressed (it also counts the columns rebuilt without that choice); --hash recomputes
  the game's ContentHash of each tile (pure Python, about 1 MB/s). Exit 1 on any refused tile.
column: one cell-column at world cell (X, Z) of a level (default 6) as the game's tile holds it: height, filled
  cells, highest filled y, SHA-256 of cells 0..1999 (the dev.28 lab's live-read fixture) and of the whole column.
cook: a lab qualification pack (terraincook.py) from a plan: changed level-6 tiles under the game's ContentHash, the
  patched root, assets/assets.json, receipt.json. Lab servers only: coarser levels and companions stay stale.

--game defaults to the installed client; a folder whose archive a mod loader patched is read through its originals.
Reads only; writes only under -o / $KILN_OUT.
"""
import hashlib
import json
import os
import random
import struct
import sys
import time
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

from . import voxeltile as vt
from . import voxelworld as vw
from .archive import Kfc


def open_game(path):
    """The game's own archive in `path` (its originals when EML patched it), else the archive as it is."""
    return Kfc.unpatched(path) or Kfc(path)


def _parts(arg):
    return [vw.SOLID, vw.FOG] if arg == "all" else [int(arg)]


def cmd_info(k, a):
    out = {"game": k.version, "layout": f"{vw.layout_of(k):#x}", "layout_known": vw.layout_of(k) == vw.LAYOUT_HASH,
           "parts": {}}
    for part in _parts(a.part):
        w = vw.load(k, part)
        levels = []
        for lv in w.levels:
            sizes = [r.hash.size for r in lv.tiles]
            levels.append({"level": lv.index, "cell_m": lv.cell_m, "tileSize": lv.tile_size, "tileCount": lv.tile_count,
                           "cells": [lv.cells[0], lv.cells[2]], "tiles": len(lv.tiles),
                           "uniqueTiles": len({r.hash for r in lv.tiles}), "bytes": sum(sizes),
                           "minBytes": min(sizes), "maxBytes": max(sizes),
                           "missing": sum(1 for r in lv.tiles if k.content_location(r.hash) is None)})
        out["parts"][part] = {"kind": w.kind, "size": w.size, "origin": w.origin, "defaultMaterial": w.default_material,
                              "rootBytes": len(w.raw), "rootSha256": hashlib.sha256(w.raw).hexdigest(),
                              "cpuDisplacement": {"hash": str(w.cpu_displacement.hash), "bytes": w.cpu_displacement.hash.size},
                              "voxelHashes": {"hash": str(w.voxel_hashes.hash), "bytes": w.voxel_hashes.hash.size},
                              "levels": levels}
    print(json.dumps(out, indent=1))


# ---------------------------------------------------------------- check
_K = None
_SUMMED = {"bytes", "columns", "blocks", "uniform", "compressed", "uniform_runs", "compressed_runs", "empty_columns",
           "padded_columns", "building_runs_without_fill", "canonical_columns", "layout_columns"}


def _worker_init(game):
    global _K
    _K = open_game(game)


def _check_one(job):
    """One tile: (part, level, index, hash text, expected dims, decode, canonical, hash) -> result row."""
    part, level, index, htext, want, decode, canonical, rehash = job
    from .archive import ContentHash
    h = ContentHash(htext)
    row = {"part": part, "level": level, "index": index, "hash": htext, "ok": False}
    try:
        data = _K.read_content(h)
        t0 = time.perf_counter()
        s = vt.check_tile(data, decode=decode)
        row.update(s)
        if (s["blocks_x"], s["blocks_z"]) != tuple(want):
            raise vt.TileFormatError(f"dimension bytes {s['blocks_x']} x {s['blocks_z']}, the root's grid says {tuple(want)}")
        t = vt.parse_tile(data)
        rebuilt = vt.build_tile(t.blocks_x, t.blocks_z, [vt.parse_column(c).to_bytes() if c else c for c in t.columns])
        if rebuilt != data:
            raise vt.TileFormatError("the structural rebuild is not byte-identical")
        if canonical:
            plain = kept = 0
            first = None
            for i, c in enumerate(t.columns):
                if not c:
                    plain += 1
                    kept += 1
                    continue
                col = vt.parse_column(c)
                blocks = col.blocks()
                plain += vt.encode_column(blocks) == c
                if vt.encode_column(blocks, col.compressed()) == c:
                    kept += 1
                elif first is None:
                    first = i
            row["canonical_columns"] = plain
            row["layout_columns"] = kept
            if kept != len(t.columns):
                raise vt.TileFormatError(f"Kiln's writer rebuilds {kept} of {len(t.columns)} columns byte for byte "
                                         f"with the game's block layout (first other: {first})")
        if rehash:
            from .contenthash import content_hash
            row["hash_ok"] = tuple(content_hash(data)) == tuple(h)
            if not row["hash_ok"]:
                raise vt.TileFormatError("the game's ContentHash of the bytes is not the reference's hash")
        row["seconds"] = round(time.perf_counter() - t0, 3)
        row["ok"] = True
    except (vt.TileFormatError, KeyError, ValueError) as e:
        row["error"] = str(e)
    return row


def cmd_check(k, a):
    levels = list(range(vw.LEVELS)) if a.level == "all" else [int(a.level)]
    jobs = []
    seen = set()
    refs = 0
    for part in _parts(a.part):
        w = vw.load(k, part)
        for level in levels:
            lv = w.levels[level]
            if a.tiles:
                idx = [int(x) for x in a.tiles.split(",")]
            elif a.all:
                idx = range(len(lv.tiles))
            else:
                rng = random.Random(f"{part}/{level}/{a.seed}")
                idx = sorted({0, len(lv.tiles) - 1} | set(rng.sample(range(len(lv.tiles)), min(a.sample, len(lv.tiles)))))
            for i in idx:
                refs += 1
                r = lv.tiles[i]
                want = lv.tile_blocks(*lv.tile_xz(i))
                dedup = (str(r.hash), tuple(want))
                if dedup in seen and not a.no_dedup:  # one blob, many references: checked once per dimensions
                    continue
                seen.add(dedup)
                jobs.append((part, level, i, str(r.hash), want, not a.no_decode, a.canonical, a.hash))
    t0 = time.time()
    rows = []
    agg = {}
    bad = 0
    with ProcessPoolExecutor(max_workers=a.jobs, initializer=_worker_init, initargs=(a.game_path,)) as ex:
        for n, row in enumerate(ex.map(_check_one, jobs, chunksize=4), 1):
            rows.append(row)
            key = f"p{row['part']}/l{row['level']}"
            g = agg.setdefault(key, {"tiles": 0, "refused": 0, "max_height": 0})
            g["tiles"] += 1
            if not row["ok"]:
                bad += 1
                g["refused"] += 1
                print(f"REFUSED {key} tile {row['index']}: {row.get('error')}", file=sys.stderr)
            for f, v in row.items():
                if f in _SUMMED and isinstance(v, int):
                    g[f] = g.get(f, 0) + v
            g["max_height"] = max(g["max_height"], row.get("max_height", 0))
            if n % 200 == 0:
                print(f"  {n}/{len(jobs)} tiles, {time.time() - t0:.0f} s", file=sys.stderr, flush=True)
    report = {"game": k.version, "decode": not a.no_decode, "canonical": a.canonical, "hash": a.hash,
              "references": refs, "tiles": len(rows), "refused": bad, "seconds": round(time.time() - t0, 1), "groups": agg}
    if a.o:
        Path(a.o).write_text(json.dumps({**report, "rows": rows}, indent=1) + "\n")
    print(json.dumps(report, indent=1))
    return 1 if bad else 0


def cmd_column(k, a):
    w = vw.load(k, a.part)
    lv = w.levels[a.level]
    index, lx, lz = w.tile_of_cell(a.x, a.z, a.level)
    data = k.read_content(lv.tiles[index].hash)
    cells = vt.TileCells.from_bytes(data).column_cells(lx, lz)
    filled = [y for y, v in enumerate(cells) if v >> 8]
    first2000 = (cells + [0] * 2000)[:2000]
    out = {"level": a.level, "tile": index, "tileXZ": lv.tile_xz(index), "local": [lx, lz], "height_cells": len(cells),
           "filled": len(filled), "highest": filled[-1] if filled else None,
           "sha256_2000": hashlib.sha256(struct.pack("<2000H", *first2000)).hexdigest(),
           "sha256": hashlib.sha256(struct.pack(f"<{len(cells)}H", *cells)).hexdigest()}
    if a.cells:
        out["cells"] = [f"{v:04x}" for v in cells]
    print(json.dumps(out, indent=1))


def cmd_cook(k, a):
    from . import terraincook
    plan = json.loads(Path(a.plan).read_text())
    exe = next((p for p in (Path(a.game_path) / n for n in ("enshrouded_server.exe", "enshrouded.exe")) if p.is_file()), None)
    sha = hashlib.sha256(exe.read_bytes()).hexdigest() if exe else None
    receipt = terraincook.cook(k, plan, a.o, exe_sha256=sha)
    print(json.dumps({"out": a.o, "tiles": receipt["tiles"], "checks": receipt["checks"]}, indent=1))


def add_parser(sub):
    p = sub.add_parser("terrain", help="the baked terrain's tiles (info, check, column, cook)")
    t = p.add_subparsers(dest="terrain_cmd", required=True)
    q = t.add_parser("info")
    q.add_argument("--part", default="all")
    q = t.add_parser("check")
    q.add_argument("--part", default="all")
    q.add_argument("--level", default="all")
    q.add_argument("--tiles", help="comma-separated tile indices (with one --level)")
    q.add_argument("--sample", type=int, default=4, help="tiles per level and part (plus the first and last)")
    q.add_argument("--seed", default="19f")
    q.add_argument("--all", action="store_true", help="every tile")
    q.add_argument("--no-decode", action="store_true", help="skip decoding compressed blocks")
    q.add_argument("--no-dedup", action="store_true", help="check a blob once per reference, not once")
    q.add_argument("--canonical", action="store_true")
    q.add_argument("--hash", action="store_true")
    q.add_argument("--jobs", type=int, default=int(os.environ.get("KILN_TERRAIN_JOBS", "2")))
    q.add_argument("-o")
    q = t.add_parser("column")
    q.add_argument("x", type=int)
    q.add_argument("z", type=int)
    q.add_argument("--part", type=int, default=vw.SOLID)
    q.add_argument("--level", type=int, default=vw.FINEST)
    q.add_argument("--cells", action="store_true")
    q = t.add_parser("cook")
    q.add_argument("plan")
    q.add_argument("-o", required=True)


def run(args):
    args.game_path = str(Path(args.game).expanduser())
    k = open_game(args.game_path)
    return {"info": cmd_info, "check": cmd_check, "column": cmd_column, "cook": cmd_cook}[args.terrain_cmd](k, args)
