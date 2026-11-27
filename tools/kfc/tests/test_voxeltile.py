# SPDX-License-Identifier: MIT
"""tools/kfc voxeltile: the baked terrain's tile envelope, column runs and 8x8x8 block codec, with no game files.

    python3 -m unittest discover -s tools/kfc/tests -p 'test_voxel*.py' -v

The hand-made vectors follow the server's reader (0x82f840 envelope, 0x845860 column runs, 0x84adc0 block lengths,
0x8514b0 block codec; dossier chapter 19f section 2.3). test_voxeltile_game.py checks the same code on the game's tiles.
"""
import random
import struct
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from kfc import voxeltile as vt  # noqa: E402
from kfc import voxelworld as vw  # noqa: E402
from kfc.archive import ContentHash  # noqa: E402


def cell(material, fill):
    return material | fill << 8


SOLID1 = cell(1, 255)
AIR = 0


class BlockCodecTest(unittest.TestCase):
    # 512 cells of material 1, fill 255, as a compressed block: a material literal and one 512-run, then the default
    # fill (255 for a terrain material) in one 512-run. Bits LSB-first: 1, (1,0)x9, 0 | 0, (1,0)x9, 0 -> 40 bits = 5 bytes,
    # then the one literal at the tail.
    ONE_RUN = bytes([0xAB, 0xAA, 0xA2, 0xAA, 0x2A, 0x01])

    def test_hand_vector_decodes(self):
        self.assertEqual(list(vt.decode_block(self.ONE_RUN)), [SOLID1] * 512)

    def test_hand_vector_is_what_the_encoder_writes(self):
        self.assertEqual(vt.encode_block([SOLID1] * 512), self.ONE_RUN)

    def test_older_material_is_reused_without_a_literal(self):
        # runs 3, 0, 3: the first is a literal; 0 is the run-before-last (the start value 0); the third 3 is the
        # run-before-last again. One material literal, no literal for the defaults (255 for 3, 0 for air).
        cells = [cell(3, 255)] * 100 + [AIR] * 200 + [cell(3, 255)] * 212
        data = vt.encode_block(cells)
        self.assertEqual(list(vt.decode_block(data)), cells)
        self.assertEqual(data[-1], 3)
        self.assertEqual(vt.block_literals(data), 1)

    def test_partial_terrain_fill_is_one_cell_per_literal(self):
        cells = [cell(5, 128)] * 4 + [cell(5, 255)] * 508
        data = vt.encode_block(cells)
        self.assertEqual(list(vt.decode_block(data)), cells)
        self.assertEqual(vt.block_literals(data), 1 + 4)  # the material, then 4 single-cell fills

    def test_building_fill_default_is_the_last_building_fill(self):
        cells = [cell(130, 60)] * 10 + [cell(131, 60)] * 10 + [cell(130, 61)] * 492
        data = vt.encode_block(cells)
        self.assertEqual(list(vt.decode_block(data)), cells)

    def test_round_trip_random_blocks(self):
        rng = random.Random(0x19F)
        for i in range(300):
            mats = [0, 1, 2, 7, 116, 128, 200, 255]
            cells = []
            while len(cells) < 512:
                m = rng.choice(mats)
                f = 0 if m == 0 and rng.random() < 0.8 else rng.choice([0, 1, 64, 128, 254, 255, 255, 255])
                cells += [cell(m, f)] * rng.randint(1, 40)
            cells = cells[:512]
            try:
                data = vt.encode_block(cells)
            except vt.EncodeError:
                continue
            self.assertLessEqual(len(data), vt.MAX_BLOCK_BYTES)
            self.assertEqual(list(vt.decode_block(data)), cells, f"block {i}")

    def test_a_block_too_varied_for_ten_bits_is_refused(self):
        cells = [cell(1 + (i % 2) * 2 + (i // 2) % 2, 1 + i % 250) for i in range(512)]
        with self.assertRaises(vt.EncodeError):
            vt.encode_block(cells)

    def test_corrupt_blocks_are_refused(self):
        good = vt.encode_block([cell(1 + i // 64, 255 if i % 3 else 100) for i in range(512)])
        bad = [
            b"",                                  # no bytes
            good[:-1],                            # the tail lost a literal: the front and back no longer meet
            good + b"\0",                         # one byte too many
            bytes([0xFF]) * 8,                    # every run says "one more bit", past the end
            self.ONE_RUN[:4] + bytes([0x00]) + self.ONE_RUN[5:],  # runs that stop short of 512 cells
        ]
        for data in bad:
            with self.assertRaises(vt.TileFormatError, msg=data.hex()):
                vt.decode_block(data)

    def test_cells_out_of_range_are_refused(self):
        with self.assertRaises(vt.EncodeError):
            vt.encode_block([0x10000] * 512)
        with self.assertRaises(vt.EncodeError):
            vt.encode_block([SOLID1] * 511)


class ColumnTest(unittest.TestCase):
    def test_empty_column(self):
        self.assertEqual(vt.encode_column([]), b"\0\0\0\0")
        col = vt.parse_column(b"\0\0\0\0")
        self.assertEqual(col.height, 0)
        self.assertEqual(col.to_bytes(), b"\0\0\0\0")
        self.assertEqual(col.blocks(), [])

    def test_hand_column(self):
        # two uniform runs: 3 blocks of material 1 (new material, no fill byte: fill 255), then 2 blocks of air (new
        # material 0, no fill byte). Controls LSB-first: gamma(3)=1,1,0 flag 0 new 1 fill 0 | gamma(2)=1,0,0 flag 0
        # new 1 fill 0 -> 12 bits = 2 control bytes; payload: 01 00.
        bits = [1, 1, 0, 0, 1, 0, 1, 0, 0, 0, 1, 0]
        ctrl = sum(b << i for i, b in enumerate(bits)).to_bytes(2, "little")
        data = struct.pack("<HH", 5, 2) + ctrl + bytes([1, 0])
        col = vt.parse_column(data)
        self.assertEqual(col.height, 5)
        self.assertEqual(col.blocks(), [SOLID1, SOLID1, SOLID1, AIR, AIR])
        self.assertEqual(col.to_bytes(), data)
        self.assertEqual(vt.encode_column(col.blocks()), data)

    def test_uniform_run_keeps_the_previous_value(self):
        # material 4 with an explicit fill 200, then a compressed block, then "same as before" (new material bit 0)
        block = vt.encode_block([cell(4, 255)] * 256 + [AIR] * 256)
        w = vt._Writer()
        w.gamma(1); w.put(0, 1); w.put(1, 1); w.put(1, 1)   # uniform, new material, with a fill byte
        w.gamma(1); w.put(1, 1); w.put(len(block), 10)       # one compressed block
        w.gamma(2); w.put(0, 1); w.put(0, 1)                 # uniform, the material and fill of the last uniform run
        ctrl = w.to_bytes()
        data = struct.pack("<HH", 4, len(ctrl)) + ctrl + bytes([4, 200]) + block
        col = vt.parse_column(data)
        self.assertEqual(col.blocks()[0], cell(4, 200))
        self.assertEqual(list(col.blocks()[1]), [cell(4, 255)] * 256 + [AIR] * 256)
        self.assertEqual(col.blocks()[2:], [cell(4, 200)] * 2)
        self.assertEqual(col.to_bytes(), data)

    def test_round_trip_random_columns(self):
        rng = random.Random(7)
        for _ in range(60):
            blocks = []
            for _ in range(rng.randint(0, 40)):
                r = rng.random()
                if r < 0.5:
                    blocks.append(rng.choice([AIR, SOLID1, cell(9, 255), cell(9, 77), cell(140, 255)]))
                else:
                    blocks.append([rng.choice([AIR, SOLID1, cell(2, 30), cell(2, 255)]) for _ in range(512)])
            data = vt.encode_column(blocks)
            got = vt.parse_column(data).blocks()
            want = [b if isinstance(b, int) else (b[0] if len(set(b)) == 1 else list(b)) for b in blocks]
            self.assertEqual([b if isinstance(b, int) else list(b) for b in got], want)

    def test_one_value_blocks_stored_compressed_keep_that_choice(self):
        # the game stores some one-value blocks compressed (a 5-byte all-air block at a column's top): the cells are
        # the same, the bytes are not; `compressed` keeps the choice
        blocks = [SOLID1, AIR, AIR]
        kept = vt.encode_column(blocks, compressed={2})
        col = vt.parse_column(kept)
        self.assertEqual([r[0] for r in col.runs], ["u", "u", "c"])
        self.assertEqual(col.compressed(), {2})
        self.assertEqual(col.runs[2][1], [bytes.fromhex("aaaaa2aa2a")])
        self.assertEqual([b if isinstance(b, int) else b[0] for b in col.blocks()], blocks)
        self.assertNotEqual(vt.encode_column(blocks), kept)
        self.assertEqual(vt.encode_column(col.blocks(), col.compressed()), kept)
        # a changed column keeps the choice for the blocks not changed, and drops it for a block that was
        tile = vt.build_tile(1, 1, [kept])
        cells = vt.TileCells.from_bytes(tile)
        cells.set(0, 0, 0, cell(5, 255))
        self.assertEqual(vt.parse_column(vt.parse_tile(cells.to_bytes()).columns[0]).compressed(), {0, 2})
        cells.set(0, 17, 0, SOLID1)
        cells.set(0, 17, 0, AIR)
        self.assertEqual(vt.parse_column(vt.parse_tile(cells.to_bytes()).columns[0]).compressed(), {0})

    def test_corrupt_columns_are_refused(self):
        good = vt.encode_column([SOLID1, [SOLID1] * 256 + [AIR] * 256, AIR])
        h, n = struct.unpack_from("<HH", good)
        bad = [
            b"\0\0\0",                                        # shorter than the header
            struct.pack("<HH", h + 100, n) + good[4:],        # says more blocks than its runs hold
            struct.pack("<HH", h, n + 1) + good[4:],          # control bytes run into the payload
            good[:-1],                                        # payload short
            good + b"\0",                                     # payload left over
        ]
        for data in bad:
            with self.assertRaises(vt.TileFormatError, msg=data.hex()):
                vt.parse_column(data, decode=True)


class TileTest(unittest.TestCase):
    def test_air_tile_of_the_dev28_lab(self):
        # the 7,170-byte tile the dev.28 lab served: 32x32 columns, each 4 zero bytes
        tile = bytes([32, 32]) + b"".join((4 * i).to_bytes(3, "little") for i in range(1024)) + b"\0" * 4096
        t = vt.parse_tile(tile)
        self.assertEqual((t.blocks_x, t.blocks_z), (32, 32))
        self.assertEqual(t.header_size, 3074)
        self.assertTrue(all(c == b"\0\0\0\0" for c in t.columns))
        self.assertEqual(t.to_bytes(), tile)
        self.assertEqual(vt.empty_tile(32, 32), tile)

    def test_edge_tile_dimensions_are_kept(self):
        cols = [vt.encode_column([SOLID1] * (i % 3)) for i in range(8 * 16)]
        data = vt.build_tile(8, 16, cols)
        t = vt.parse_tile(data)
        self.assertEqual((t.blocks_x, t.blocks_z, t.header_size), (8, 16, 386))
        self.assertEqual(t.to_bytes(), data)

    def test_cells_read_and_write(self):
        cells = vt.TileCells.empty(2, 2)
        cells.set(3, 17, 12, cell(7, 255))
        cells.set(15, 0, 15, cell(1, 40))
        data = cells.to_bytes()
        back = vt.TileCells.from_bytes(data)
        self.assertEqual(back.get(3, 17, 12), cell(7, 255))
        self.assertEqual(back.get(15, 0, 15), cell(1, 40))
        self.assertEqual(back.get(3, 16, 12), AIR)
        self.assertEqual(back.get(0, 100, 0), AIR)       # above a column's top: air
        self.assertEqual(back.column_height(0, 1), 3)    # block-column (0, 1) reaches y 17 -> 3 blocks
        self.assertEqual(len(back.column_cells(3, 12)), 24)

    def test_untouched_columns_keep_their_bytes(self):
        # a column the cooker did not touch is copied as it was, even where the encoder would write it otherwise
        odd = struct.pack("<HH", 1, 1) + bytes([0b00001100]) + bytes([1, 255])  # uniform, new material 1, fill 255 given
        data = vt.build_tile(2, 1, [odd, b"\0\0\0\0"])
        cells = vt.TileCells.from_bytes(data)
        cells.set(9, 0, 0, SOLID1)                       # block-column (1, 0) only
        out = vt.parse_tile(cells.to_bytes())
        self.assertEqual(out.columns[0], odd)
        self.assertEqual([list(b) for b in vt.parse_column(out.columns[1]).blocks()],
                         [[SOLID1 if i == 1 else AIR for i in range(512)]])

    def test_corrupt_tiles_are_refused(self):
        good = vt.build_tile(2, 2, [vt.encode_column([SOLID1] * k) for k in (1, 2, 0, 3)])
        hdr = 2 + 3 * 4

        def with_offset(i, value):
            b = bytearray(good)
            b[2 + 3 * i:5 + 3 * i] = value.to_bytes(3, "little")
            return bytes(b)

        bad = {
            "empty": b"",
            "zero columns": bytes([0, 4]) + good[2:],
            "header cut": good[:hdr - 1],
            "first offset not 0": with_offset(0, 1),
            "offsets go back": with_offset(2, 0),
            "offset past the end": with_offset(3, len(good)),
            "last column cut": good[:-1],
            "column bytes left over": good + b"\0",
        }
        for why, data in bad.items():
            with self.assertRaises(vt.TileFormatError, msg=why):
                vt.check_tile(data)

    def test_check_counts(self):
        data = vt.build_tile(2, 1, [vt.encode_column([SOLID1, [SOLID1] * 300 + [AIR] * 212]), b"\0\0\0\0"])
        s = vt.check_tile(data)
        self.assertEqual(s["columns"], 2)
        self.assertEqual(s["blocks"], 2)
        self.assertEqual(s["uniform"], 1)
        self.assertEqual(s["compressed"], 1)
        self.assertEqual(s["bytes"], len(data))


def synthetic_root(size=(320, 64, 192), tile_sizes=(128, 128, 128, 128, 128, 256, 256)):
    """A VoxelWorldResource in the reflected layout: header, seven levels, then the tile arrays."""
    head = bytearray(vw.HEADER_SIZE)
    struct.pack_into("<3I", head, 4, *size)   # type 0 (solid) at +0, origin (0, 0, 0) at +0x10
    arrays = bytearray()
    refs = []
    for li, ts in enumerate(tile_sizes):
        cells = [s * (1 << li) // 32 for s in size]
        tc = (-(-cells[0] // ts), -(-cells[2] // ts))
        at = vw.LEVELS_AT + vw.LEVEL_STRIDE * li
        first = vw.HEADER_SIZE + len(arrays)
        struct.pack_into("<4IiI", head, at, ts, ts, *tc, first - (at + 16), tc[0] * tc[1])
        for i in range(tc[0] * tc[1]):
            arrays += struct.pack("<4IIIQ", 7170, li, i, 0xAB, 0x11, 0x22, 0x33)
            refs.append((li, i))
    return bytes(head + arrays), refs


class WorldTest(unittest.TestCase):
    def test_grid_edges_and_patch(self):
        raw, refs = synthetic_root()
        w = vw.VoxelWorld(raw)
        self.assertEqual([lv.tile_count for lv in w.levels], [(1, 1), (1, 1), (1, 1), (1, 1), (2, 1), (2, 1), (3, 2)])
        self.assertEqual(w.levels[0].tile_blocks(0, 0), (2, 1))        # 10 x 6 cells: the tile's own, not 128
        self.assertEqual(w.levels[4].tile_blocks(1, 0), (4, 12))       # 160 x 96 cells: the last tile's remainder
        self.assertEqual(w.levels[6].tile_blocks(0, 0), (32, 32))
        self.assertEqual(w.tile_of_cell(600, 300), (5, 88, 44))
        new = ContentHash(7170, 1, 2, 3)
        out = vw.VoxelWorld(w.patched({(6, 5): new}))
        self.assertEqual(out.levels[6].tiles[5].hash, new)
        self.assertEqual(out.levels[6].tiles[5].sort_key, 0x33)
        self.assertEqual(sum(a != b for a, b in zip(out.raw, raw)), 3)  # one byte each of h0, h1, h2 (6, 5, 0xab)
        self.assertEqual(len(refs), len(list(w.refs())))

    def test_roots_this_reader_refuses(self):
        raw, _ = synthetic_root()
        with self.assertRaises(vw.WorldFormatError):
            vw.VoxelWorld(raw[:vw.HEADER_SIZE - 1])
        bad = bytearray(raw)
        struct.pack_into("<I", bad, vw.LEVELS_AT + vw.LEVEL_STRIDE * 6 + 8, 4)   # a grid that does not cover the world
        with self.assertRaises(vw.WorldFormatError):
            vw.VoxelWorld(bytes(bad))
        bad = bytearray(raw)
        struct.pack_into("<i", bad, vw.LEVELS_AT + 16, 1 << 30)                   # a tile array outside the bytes
        with self.assertRaises(vw.WorldFormatError):
            vw.VoxelWorld(bytes(bad))


if __name__ == "__main__":
    unittest.main()
