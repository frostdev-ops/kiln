# SPDX-License-Identifier: MIT
"""tools/kfc voxeltile / voxelworld / terraincook on the game's own baked terrain (skipped without an installed game).

    python3 -m unittest discover -s tools/kfc/tests -p 'test_voxel*.py' -v
    KILN_TERRAIN_GAME=~/srv/enshrouded/lab2 ...      a server folder instead of the installed client
    KILN_TERRAIN_SAMPLE=12 ...                       more tiles per level and part (default 3, plus fixed ones)

Server 26430253 and client 26430625 ship byte-identical terrain resources (dossier chapter 19f section 1); either
folder works. A folder whose archive EML patched is read through its originals. The whole world is checked by
`kfc.py terrain check --all` (docs/re/build.md section 9), not here.
"""
import hashlib
import os
import random
import struct
import sys
import tempfile
import json
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from kfc import terraincook as tc  # noqa: E402
from kfc import voxeltile as vt  # noqa: E402
from kfc import voxelworld as vw  # noqa: E402
from kfc.archive import ContentHash, Kfc  # noqa: E402
from kfc.contenthash import content_hash  # noqa: E402

GAME = Path(os.environ.get("KILN_TERRAIN_GAME") or Path.home() / ".local/share/Steam/steamapps/common/Enshrouded").expanduser()
HAS_GAME = GAME.is_dir() and any(GAME.glob("*.kfc"))
SAMPLE = int(os.environ.get("KILN_TERRAIN_SAMPLE", "3"))

# the dev.28 lab's live read on a fresh server world (dossier 19f section 1): cells y 0..1999 of world cell column
# (6048, 3104) = level-6 tile 983 (23, 12), local (160, 32); 1,805 filled cells, highest 1804
LAB_COLUMN = (6048, 3104)
LAB_COLUMN_SHA256 = "fde3693bf8f2d8e43c181e5def995d99d4b61e2366f5d28c8d5283654b16e004"
# the shipped all-air level-6 tile: the game's own ContentHash of the 7,170 bytes, referenced by fog level 6
AIR_TILE_NATIVE = "021c000089b408f37ff5bfd7deb84d26"


def open_game():
    return Kfc.unpatched(GAME) or Kfc(GAME)


@unittest.skipUnless(HAS_GAME, f"no game folder with a .kfc at {GAME}")
class GameTerrainTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.k = open_game()
        cls.worlds = {p: vw.load(cls.k, p) for p in (vw.SOLID, vw.FOG)}

    def sample(self, part, level):
        lv = self.worlds[part].levels[level]
        rng = random.Random(f"{part}/{level}")
        fixed = {0, len(lv.tiles) - 1}
        if level == 0:
            fixed |= {2, 6, 8}          # the 8 x 16, 16 x 8 and 8 x 8 edge tiles
        if level == 6:
            fixed |= {983}              # the dev.28 lab's tile
        return sorted(fixed | set(rng.sample(range(len(lv.tiles)), min(SAMPLE, len(lv.tiles)))))

    def test_roots(self):
        solid, fog = self.worlds[vw.SOLID], self.worlds[vw.FOG]
        self.assertEqual((solid.kind, fog.kind), ("solid", "fog"))
        self.assertEqual(solid.size, (10240, 2508, 10240))
        self.assertEqual(fog.size, (10240, 2188, 10240))
        grids = [(3, 3), (5, 5), (10, 10), (20, 20), (40, 40), (40, 40), (80, 80)]
        for w in (solid, fog):
            self.assertEqual([lv.tile_count for lv in w.levels], grids)
            self.assertEqual([lv.tile_size[0] for lv in w.levels], [128] * 5 + [256] * 2)
            for level, index, r in w.refs():
                self.assertIsNotNone(self.k.content_location(r.hash), f"{w.kind} level {level} tile {index}")
        self.assertEqual(solid.voxel_hashes.hash.size, 6553600)
        self.assertEqual(solid.cpu_displacement.hash.size, 98680)
        self.assertEqual(sorted({solid.levels[0].tile_blocks(*solid.levels[0].tile_xz(i)) for i in range(9)}),
                         [(8, 8), (8, 16), (16, 8), (16, 16)])

    def test_tiles_read_rebuild_and_reencode_byte_for_byte(self):
        """Every sampled tile of both parts and all seven levels: the reader's rules hold, the dimension bytes are the
        root's grid, the structural rebuild and Kiln's writer (from the decoded cells and the game's choice of which
        one-value blocks are stored compressed) give the same bytes, and the game's ContentHash of the bytes is the
        reference's key."""
        hashed = plain = columns = 0
        for part, w in self.worlds.items():
            for lv in w.levels:
                for i in self.sample(part, lv.index):
                    where = f"{w.kind} level {lv.index} tile {i}"
                    ref = lv.tiles[i]
                    data = self.k.read_content(ref.hash)
                    s = vt.check_tile(data)
                    self.assertEqual((s["blocks_x"], s["blocks_z"]), lv.tile_blocks(*lv.tile_xz(i)), where)
                    t = vt.parse_tile(data)
                    cells = vt.TileCells.from_bytes(data)
                    for ci, c in enumerate(t.columns):
                        col = vt.parse_column(c)
                        self.assertEqual(col.to_bytes(), c, f"{where} column {ci}: structural rebuild")
                        blocks = col.blocks()
                        self.assertEqual(vt.encode_column(blocks, col.compressed()), c, f"{where} column {ci}: re-encode")
                        plain += vt.encode_column(blocks) == c
                        columns += 1
                        cells._dirty.add(ci)
                    self.assertEqual(cells.to_bytes(), data, f"{where}: whole tile re-encoded")
                    if len(data) <= 400_000 or lv.index == 6 and i == 983:   # pure-Python hash: ~1 MB/s
                        self.assertEqual(ContentHash(*content_hash(data)), ref.hash, where)
                        hashed += 1
        self.assertGreater(hashed, 20)
        # without the game's layout choice most columns still come out the same (the rest differ only in one-value
        # blocks stored uniform instead of compressed)
        self.assertGreater(plain / columns, 0.5)

    def test_the_dev28_lab_column(self):
        x, z = LAB_COLUMN
        w = self.worlds[vw.SOLID]
        index, lx, lz = w.tile_of_cell(x, z)
        self.assertEqual((index, lx, lz), (983, 160, 32))
        cells = vt.TileCells.from_bytes(self.k.read_content(w.levels[6].tiles[index].hash)).column_cells(lx, lz)
        cells = (cells + [0] * 2000)[:2000]
        filled = [y for y, v in enumerate(cells) if v >> 8]
        self.assertEqual((len(filled), filled[-1]), (1805, 1804))
        self.assertEqual(hashlib.sha256(struct.pack("<2000H", *cells)).hexdigest(), LAB_COLUMN_SHA256)

    def test_the_shipped_air_tile_is_what_empty_tile_writes(self):
        air = vt.empty_tile(32, 32)
        h = ContentHash(*content_hash(air))
        self.assertEqual(str(h), AIR_TILE_NATIVE)
        self.assertEqual(self.k.read_content(h), air)
        uses = sum(1 for r in self.worlds[vw.FOG].levels[6].tiles if str(r.hash) == AIR_TILE_NATIVE)
        self.assertGreater(uses, 0)

    def test_damaged_real_tiles_are_refused(self):
        w = self.worlds[vw.SOLID]
        data = self.k.read_content(w.levels[6].tiles[983].hash)
        t = vt.parse_tile(data)
        head = t.header_size
        col0 = t.columns[0]
        ctrl = struct.unpack_from("<H", col0, 2)[0]

        def put(at, b):
            out = bytearray(data)
            out[at:at + len(b)] = b
            return bytes(out)

        off5 = int.from_bytes(data[2 + 15:5 + 15], "little")
        cases = {
            "an offset moved back": put(2 + 15, (off5 - 1).to_bytes(3, "little")),
            "an offset past the payload": put(2 + 3 * 1023, (len(data) - head + 1).to_bytes(3, "little")),
            "a column's control length": put(head + 2, struct.pack("<H", ctrl + 1)),
            "a column's height": put(head, struct.pack("<H", 0xFFFF)),
            "the tile cut short": data[:-7],
            "one byte too many": data + b"\0",
        }
        # a compressed block's bytes: flip the bits of its first control byte
        for ci, c in enumerate(t.columns):
            col = vt.parse_column(c)
            runs = [r for r in col.runs if r[0] == "c"]
            if runs:
                blob = runs[0][1][0]
                at = head + sum(len(x) for x in t.columns[:ci]) + c.index(blob)
                cases["a compressed block's bits"] = put(at, bytes([blob[0] ^ 0xFF]))
                break
        for why, bad in cases.items():
            with self.assertRaises(vt.TileFormatError, msg=why):
                vt.check_tile(bad)

    def test_cook_a_pack(self):
        """A plan that changes tile 983 (a seeded random box, a pillar above the old ground) and empties tile 1063:
        the pack's tiles pass the checker, carry the game's ContentHash, the root points at them, the emptied tile
        reuses the shipped air blob, and the checks are the cells the plan asked for."""
        plan = {
            "format": 1, "part": 0,
            "edits": [
                {"kind": "random", "box": {"x": 5960, "y": 1780, "z": 3140, "sizeX": 24, "sizeY": 40, "sizeZ": 20},
                 "seed": 25, "palette": [{"material": 0}, {"material": 3}, {"material": 3, "fill": 128},
                                         {"material": 17, "fill": 9}, {"material": 150, "fill": 200}]},
                {"kind": "fill", "box": {"x": 6100, "y": 1700, "z": 3300, "sizeX": 8, "sizeY": 500, "sizeZ": 8},
                 "cell": {"material": 2}},
                {"kind": "empty", "tile": 1063},
            ],
            "checks": [
                {"name": "random", "box": {"x": 5960, "y": 1780, "z": 3140, "sizeX": 24, "sizeY": 40, "sizeZ": 20}},
                {"name": "pillar", "box": {"x": 6100, "y": 1700, "z": 3300, "sizeX": 8, "sizeY": 500, "sizeZ": 8}},
                {"name": "air", "box": {"x": 5900, "y": 0, "z": 3400, "sizeX": 4, "sizeY": 2000, "sizeZ": 4}},
                {"name": "control", "box": {"x": 6200, "y": 0, "z": 3104, "sizeX": 1, "sizeY": 2000, "sizeZ": 1}},
            ],
        }
        with tempfile.TemporaryDirectory() as d:
            r = tc.cook(self.k, plan, d)
            manifest = json.loads((Path(d) / "assets/assets.json").read_text())
            rows = {t["index"]: t for t in r["tiles"]}
            self.assertEqual(sorted(rows), [983, 1063])
            self.assertTrue(rows[1063]["reusesShippedBlob"])
            self.assertEqual(rows[1063]["new"], AIR_TILE_NATIVE)
            self.assertFalse(rows[983]["reusesShippedBlob"])
            blobs = [e for e in manifest["entries"] if e["kind"] == "blob"]
            self.assertEqual(len(blobs), 1)
            tile = (Path(d) / "assets" / blobs[0]["file"]).read_bytes()
            vt.check_tile(tile)
            self.assertEqual(ContentHash(blobs[0]["hash"]), ContentHash(*content_hash(tile)))
            # as kiln.assets reads it (sscanf "%u-%x-%x-%x"): a hex size it does not read, and then serves the blob
            # under a derived key instead (the first lab run: "Could not find content ... in kfc!")
            size, h0, h1, h2 = blobs[0]["hash"].split("-")
            self.assertEqual(ContentHash(int(size, 10), int(h0, 16), int(h1, 16), int(h2, 16)), ContentHash(*content_hash(tile)))
            self.assertEqual(str(ContentHash(blobs[0]["hash"])), rows[983]["new"])
            root = vw.VoxelWorld((Path(d) / "assets/terrain/world-0.bin").read_bytes())
            self.assertEqual(str(root.levels[6].tiles[983].hash), rows[983]["new"])
            self.assertEqual(str(root.levels[6].tiles[1063].hash), AIR_TILE_NATIVE)
            old = self.worlds[vw.SOLID]
            changed = [(lv, i) for lv, i, ref in root.refs() if ref.hash != old.levels[lv].tiles[i].hash]
            self.assertEqual(changed, [(6, 983), (6, 1063)])
            self.assertEqual(sum(a != b for a, b in zip(root.raw, old.raw)) <= 32, True)
            # the checks: what the plan asked for, read back independently
            rng = random.Random(25)
            pal = [0x0000, 0xFF03, 0x8003, 0x0911, 0xC896]
            want = [pal[rng.randrange(5)] for _ in range(24 * 40 * 20)]
            got = {c["name"]: c for c in r["checks"]}
            self.assertEqual(got["random"]["sha256"], tc.cells_sha256(want))
            self.assertEqual(got["pillar"]["sha256"], tc.cells_sha256([0xFF02] * (8 * 500 * 8)))
            self.assertEqual(got["air"]["sha256"], tc.cells_sha256([0] * (4 * 2000 * 4)))
            # the control column is tile 984's, untouched: as the game's tile holds it
            ctl = vt.TileCells.from_bytes(self.k.read_content(old.levels[6].tiles[984].hash)).column_cells(6200 - 6144, 3104 - 3072)
            self.assertEqual(got["control"]["sha256"], tc.cells_sha256((ctl + [0] * 2000)[:2000]))
            self.assertGreater(got["control"]["matter"], 100)
            # the untouched columns of tile 983 kept their bytes
            new_t, old_t = vt.parse_tile(tile), vt.parse_tile(self.k.read_content(old.levels[6].tiles[983].hash))
            same = sum(a == b for a, b in zip(new_t.columns, old_t.columns))
            self.assertEqual(same, 1024 - rows[983]["changedColumns"])

    def test_plans_the_cooker_refuses(self):
        base = {"format": 1, "part": 0, "edits": []}
        bad = [
            {**base, "edits": [{"kind": "fill", "box": {"x": -1, "y": 0, "z": 0, "sizeX": 1, "sizeY": 1, "sizeZ": 1},
                                "cell": {"material": 1}}]},
            {**base, "edits": [{"kind": "fill", "box": {"x": 0, "y": 0, "z": 0, "sizeX": 1, "sizeY": 1, "sizeZ": 1},
                                "cell": {"material": 300}}]},
            {**base, "edits": [{"kind": "melt", "box": {}}]},
            {**base, "edits": [{"kind": "empty", "tile": 6400}]},
            {**base, "edits": [{"kind": "empty", "tile": 5}, {"kind": "fill", "cell": {"material": 1},
                                "box": {"x": 1300, "y": 0, "z": 0, "sizeX": 1, "sizeY": 1, "sizeZ": 1}}]},
            {**base, "edits": [{"kind": "fill", "box": {"x": 6000, "y": 5016, "z": 3100, "sizeX": 1, "sizeY": 1,
                                                       "sizeZ": 1}, "cell": {"material": 1}}]},   # above the world
            {**base, "part": 2},
            {**base, "extra": 1},
            base,
        ]
        for plan in bad:
            with tempfile.TemporaryDirectory() as d, self.assertRaises(tc.PlanError, msg=json.dumps(plan)):
                tc.cook(self.k, plan, d)


if __name__ == "__main__":
    unittest.main()
