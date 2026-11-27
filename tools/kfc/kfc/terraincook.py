# SPDX-License-Identifier: MIT
"""A qualification pack for baked terrain: changed level-6 tiles under the game's own ContentHash, the patched world
root and a kiln.assets manifest, for a dedicated lab server only (dossier chapter 19f section 4, route B step 1).

Not a terrain feature and nothing a mod uses: it writes what the chapter's qualification step needs and refuses what
the chapter does not prove. Only level 6 (the server's read and write level) is changed: the coarser levels, the
voxelHashes and CPU-displacement companions, fog, biome map, scene entities and map pictures stay as shipped, so a
game client would show stale distant terrain. The pack names those gaps in its receipt.

Plan (JSON, world cells at level 6 as kiln.build's CellBox uses them: x, y, z and sizeX, sizeY, sizeZ):

    {"format": 1, "part": 0,
     "edits": [
       {"kind": "fill", "box": {...}, "cell": {"material": 2, "fill": 255}},
       {"kind": "random", "box": {...}, "seed": 25, "palette": [{"material": 3}, {"material": 3, "fill": 128}, ...]},
       {"kind": "empty", "tile": 1063}
     ],
     "checks": [{"name": "random-box", "box": {...}}]}

- `fill`: every cell of the box becomes `cell` (fill defaults to 255 for matter, 0 for air).
- `random`: every cell a seeded choice from `palette` (random.Random(seed), cells in CellBox order).
- `empty`: the whole tile becomes air (height-0 columns). Its bytes are the game's own air tile, so when the game ships
  that blob already (fog level 6 does) the pack reuses it by hash and adds no blob.
- `checks`: boxes whose cells the receipt records (SHA-256 of the u16 cells, little-endian, x fastest, then y, then z),
  read back from the pack's own tiles; a lab probe compares the server's reads with them.

Output folder: assets/assets.json (a `resource` override of the root, a `blob` with an explicit `hash` per new tile),
assets/terrain/*.bin, and receipt.json (game build, base fingerprints, every tile's old and new hash, the checks).
"""
import hashlib
import json
import random
import struct
from pathlib import Path

from . import voxeltile as vt
from . import voxelworld as vw
from .archive import ContentHash
from .contenthash import content_hash

FORMAT = 1
PLAN_KEYS = {"format", "part", "guid", "edits", "checks"}
EDIT_KEYS = {"fill": {"kind", "box", "cell"}, "random": {"kind", "box", "seed", "palette"}, "empty": {"kind", "tile"}}
MAX_EDIT_CELLS = 4 << 20


class PlanError(ValueError):
    """A plan the cooker refuses (unknown key, a box outside the world, a cell value out of range)."""


def _box(d, where):
    keys = {"x", "y", "z", "sizeX", "sizeY", "sizeZ"}
    if not isinstance(d, dict) or set(d) != keys or not all(isinstance(d[k], int) for k in keys):
        raise PlanError(f"{where}: a box is {{x, y, z, sizeX, sizeY, sizeZ}} (integers)")
    if min(d["sizeX"], d["sizeY"], d["sizeZ"]) < 1:
        raise PlanError(f"{where}: an empty box")
    return d["x"], d["y"], d["z"], d["sizeX"], d["sizeY"], d["sizeZ"]


def _cell(d, where):
    if not isinstance(d, dict) or not set(d) <= {"material", "fill"} or "material" not in d:
        raise PlanError(f"{where}: a cell is {{material, fill}}")
    m = d["material"]
    f = d.get("fill", 255 if m else 0)
    if not (isinstance(m, int) and isinstance(f, int) and 0 <= m <= 255 and 0 <= f <= 255):
        raise PlanError(f"{where}: material and fill are 0..255")
    return m | f << 8


def box_cells(box):
    x0, y0, z0, sx, sy, sz = box
    for z in range(z0, z0 + sz):
        for y in range(y0, y0 + sy):
            for x in range(x0, x0 + sx):
                yield x, y, z


def cells_sha256(values):
    return hashlib.sha256(struct.pack(f"<{len(values)}H", *values)).hexdigest()


class Cooker:
    """Applies a plan to level-6 tiles of one root, read from a Kfc."""

    def __init__(self, kfc, part=vw.SOLID, guid=vw.MAIN_WORLD):
        self.kfc = kfc
        self.part, self.guid = part, guid
        self.world = vw.load(kfc, part, guid)
        self.level = self.world.levels[vw.FINEST]
        self.tiles = {}          # tile index -> TileCells
        self.emptied = set()

    def tile(self, index):
        t = self.tiles.get(index)
        if t is None:
            data = self.kfc.read_content(self.level.tiles[index].hash)
            t = self.tiles[index] = vt.TileCells.from_bytes(data)
            want = self.level.tile_blocks(*self.level.tile_xz(index))
            if (t.blocks_x, t.blocks_z) != want:
                raise vt.TileFormatError(f"tile {index}: {t.blocks_x} x {t.blocks_z} block-columns, the root says {want}")
        return t

    def _locate(self, x, y, z):
        lv = self.level
        if not (0 <= x < lv.cells[0] and 0 <= y < lv.cells[1] and 0 <= z < lv.cells[2]):
            raise PlanError(f"cell ({x}, {y}, {z}) outside the world's {lv.cells[0]} x {lv.cells[1]} x {lv.cells[2]} cells")
        index, lx, lz = self.world.tile_of_cell(x, z)
        if index in self.emptied:
            raise PlanError(f"cell ({x}, {y}, {z}) is in tile {index}, which the plan empties")
        return index, lx, lz

    def set(self, x, y, z, value):
        index, lx, lz = self._locate(x, y, z)
        self.tile(index).set(lx, y, lz, value)

    def apply(self, plan):
        if not isinstance(plan, dict) or not set(plan) <= PLAN_KEYS or plan.get("format") != FORMAT:
            raise PlanError(f"a plan is {{format: {FORMAT}, part, edits, checks}}")
        edits = plan.get("edits") or []
        for n, e in enumerate(edits):
            where = f"edit {n}"
            kind = e.get("kind") if isinstance(e, dict) else None
            if kind not in EDIT_KEYS or not set(e) <= EDIT_KEYS[kind] or "kind" not in e:
                raise PlanError(f"{where}: kind fill, random or empty, with {sorted(EDIT_KEYS.get(kind, ()))}")
            if kind == "empty":
                index = e.get("tile")
                if not isinstance(index, int) or not 0 <= index < len(self.level.tiles):
                    raise PlanError(f"{where}: tile index 0..{len(self.level.tiles) - 1}")
                if index in self.tiles:
                    raise PlanError(f"{where}: tile {index} is edited and emptied")
                self.emptied.add(index)
                continue
            box = _box(e.get("box"), where)
            if box[3] * box[4] * box[5] > MAX_EDIT_CELLS:
                raise PlanError(f"{where}: more than {MAX_EDIT_CELLS} cells")
            if kind == "fill":
                v = _cell(e.get("cell"), where)
                for x, y, z in box_cells(box):
                    self.set(x, y, z, v)
            else:
                pal = e.get("palette")
                if not isinstance(pal, list) or not pal:
                    raise PlanError(f"{where}: a palette of cells")
                vals = [_cell(p, f"{where} palette") for p in pal]
                seed = e.get("seed")
                if not isinstance(seed, int):
                    raise PlanError(f"{where}: an integer seed")
                rng = random.Random(seed)
                for x, y, z in box_cells(box):
                    self.set(x, y, z, vals[rng.randrange(len(vals))])
        return self

    def tile_bytes(self):
        """{tile index: new bytes} for every changed or emptied tile."""
        out = {i: t.to_bytes() for i, t in self.tiles.items() if t.changed_columns}
        for i in self.emptied:
            bx, bz = self.level.tile_blocks(*self.level.tile_xz(i))
            out[i] = vt.empty_tile(bx, bz)
        return out


def cook(kfc, plan, out, exe_sha256=None):
    """Write the pack for `plan` to folder `out`; returns the receipt (also out/receipt.json). `exe_sha256`: the game
    executable the pack is for (recorded in the receipt)."""
    if not isinstance(plan, dict):
        raise PlanError("a plan is a JSON object")
    part, guid = plan.get("part", vw.SOLID), plan.get("guid", vw.MAIN_WORLD)
    if part not in (vw.SOLID, vw.FOG) or not isinstance(guid, str):
        raise PlanError("part is 0 (solid) or 1 (fog); guid a resource GUID")
    c = Cooker(kfc, part, guid)
    c.apply(plan)
    new = c.tile_bytes()
    if not new:
        raise PlanError("the plan changes no tile")
    out = Path(out)
    assets = out / "assets"
    (assets / "terrain").mkdir(parents=True, exist_ok=True)
    entries, tiles, hashes = [], [], {}
    for index in sorted(new):
        data = new[index]
        vt.check_tile(data)                                  # the pack never holds a tile the reader would refuse
        h = ContentHash(*content_hash(data))
        hashes[(vw.FINEST, index)] = h
        shipped = kfc.content_location(h) is not None
        row = {"level": vw.FINEST, "index": index, "tile": list(c.level.tile_xz(index)),
               "old": str(c.level.tiles[index].hash), "new": str(h), "bytes": len(data),
               "reusesShippedBlob": shipped, "emptied": index in c.emptied}
        if index in c.tiles:
            row["changedColumns"] = len(c.tiles[index].changed_columns)
        if not shipped:
            name = f"terrain/l6-{index}.bin"
            (assets / name).write_bytes(data)
            entries.append({"kind": "blob", "hash": h.assets_text(), "file": name})
            row["file"] = name
        tiles.append(row)
    root = c.world.patched(hashes)
    back = vw.VoxelWorld(root)
    for (level, index), h in hashes.items():
        if back.levels[level].tiles[index].hash != h:  # pragma: no cover
            raise AssertionError("patched root does not hold the new hash")
    root_name = f"terrain/world-{c.part}.bin"
    (assets / root_name).write_bytes(root)
    entries.insert(0, {"kind": "resource", "guid": c.guid, "type": vw.TYPE_NAME, "part": c.part, "mode": "override",
                       "file": root_name})
    (assets / "assets.json").write_text(json.dumps({"format": 1, "entries": entries}, indent=2) + "\n")

    # the checks, from the pack's own bytes (not from the plan): a fresh reader over the new tiles
    read = {i: vt.TileCells.from_bytes(b) for i, b in new.items()}
    checks = []
    for n, ch in enumerate(plan.get("checks") or []):
        if not isinstance(ch, dict) or set(ch) != {"name", "box"} or not isinstance(ch["name"], str):
            raise PlanError(f"check {n}: {{name, box}}")
        box = _box(ch["box"], f"check {n}")
        if box[3] * box[4] * box[5] > MAX_EDIT_CELLS:
            raise PlanError(f"check {n}: more than {MAX_EDIT_CELLS} cells")
        vals = []
        for x, y, z in box_cells(box):
            index, lx, lz = c.world.tile_of_cell(x, z)
            t = read.get(index) or c.tile(index)
            vals.append(t.get(lx, y, lz))
        checks.append({"name": ch["name"], "box": dict(zip(("x", "y", "z", "sizeX", "sizeY", "sizeZ"), box)),
                       "cells": len(vals), "matter": sum(1 for v in vals if v >> 8), "sha256": cells_sha256(vals)})
    receipt = {
        "format": FORMAT, "kind": "kiln terrain qualification pack (lab only)",
        "game": kfc.version, "exe_sha256": exe_sha256, "layout": f"{vw.layout_of(kfc):#x}",
        "world": {"guid": c.guid, "part": c.part, "kind": c.world.kind, "rootSha256": hashlib.sha256(c.world.raw).hexdigest(),
                  "patchedRootSha256": hashlib.sha256(root).hexdigest()},
        "hashRule": "the game's ContentHash (kfc.contenthash) in each reference and as each blob's explicit hash",
        "tiles": tiles, "checks": checks,
        "stale": ["levels 0-5 (the coarser terrain a client shows at a distance)", "voxelHashes", "cpuDisplacement",
                  "fog (part 1)", "BiomeMapResource", "scene entities", "map relief/detail textures"],
    }
    (out / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    return receipt
