# SPDX-License-Identifier: MIT
"""The baked terrain's tiles: tile envelope, column runs and the 8x8x8 block codec, read and written (clean-room).

Kiln's own reading of the server's voxel reader (server 26430253: tile envelope 0x82f840, column runs 0x845860, block
lengths 0x84adc0, block codec 0x8514b0; the client's 0x10ab270 / 0x10c6170 / 0x10cc7d0 are the same code). Dossier
chapter 19f section 2.3 and docs/re/build.md section 9 describe it; nothing here is taken from another decoder.

A cell is a u16: low byte material (0 air, 1..127 terrain, 128..255 building blocks), high byte fill (0 no matter,
255 solid). Cells inside an 8x8x8 block go x fastest, then y, then z.

Tile (one content blob per tile and level, little-endian):

    u8 blocks_x; u8 blocks_z;               block-columns of 8x8 cells; per tile (level 0's edge tiles are smaller)
    u24 offsets[blocks_x * blocks_z];       column i starts at payload + offsets[i]; it ends where column i+1 starts
    u8 payload[];                           (the payload starts at 2 + 3 * blocks_x * blocks_z, not at 0)

Column i is block-column (i % blocks_x, i // blocks_x):

    u16 height;                             8-cell blocks from y = 0 up (above them: air)
    u16 control_bytes;
    u8 controls[control_bytes];             bits, least significant first
    u8 payload[];                           consumed in run order

Controls: runs until `height` blocks. A run is gamma(count), then a flag: 0 = uniform blocks, then a "new value" bit
(0: the value of the column's last uniform run, at first air) and, after a 1, a "fill given" bit; the payload holds the
material byte and then the fill byte if given (no fill byte: 0 for air, 255 for any other material). Flag 1 = `count`
compressed blocks, each a 10-bit byte length; the payload holds the blocks one after the other.

gamma(n), n >= 1: the bits of n after its leading 1, most significant first, each written as (1, bit); then a 0.

A compressed block reads bits from its front and literal bytes from its back (the last byte first):
- material runs until 512 cells: flag 1 = a literal material, 0 = the material of the run before the last one (at
  first 0); then gamma(run length);
- fill runs until 512 cells, the material being that of the run's first cell: flag 1 = a literal fill; for a terrain
  material (< 128) with a fill of 1..254 that is one cell and no run length; for a building material (>= 128) it is
  that material's next default. Flag 0 = the default fill: 0 for air, 255 for terrain, the last literal building
  fill (at first 0) for building materials. Then gamma(run length).
- the front's bits (rounded up to bytes) end exactly where the literals begin; anything else is a decode error (9).

The reader refills its bit window to at least 25 bits before each run header and does not refill inside one, so a
writer keeps every run header within 25 bits. A compressed block's length is a 10-bit field: a block that packs to
more than 1,023 bytes cannot be written (EncodeError); the game's reader would misread it.
"""
import struct
import sys
from array import array

BLOCK_EDGE = 8
BLOCK_CELLS = 512
MAX_BLOCK_BYTES = 1023          # 10-bit length
MAX_HEIGHT = 0xFFFF             # u16 blocks per column
MAX_CONTROL_BYTES = 0xFFFF      # u16
MAX_OFFSET = 0xFFFFFF           # u24
RUN_WINDOW = 25                 # bits the reader has in its window at a run header
MAX_COLUMN_RUN = 2047           # gamma(2047) + flag + new value + fill given = 24 bits: fits that window


class TileFormatError(ValueError):
    """Bytes the game's reader would misread or that are not a tile: refused, never guessed."""


class EncodeError(ValueError):
    """Cells this format cannot hold: a value out of range, a block that packs to more than 1,023 bytes."""


def default_fill(material, building_fill=0):
    """The fill a block's default (flag 0) gives a cell of `material`."""
    return 0 if material == 0 else (255 if material < 128 else building_fill)


def uniform_fill(material):
    """The fill of a uniform run that gives no fill byte."""
    return 255 if material else 0


# ---------------------------------------------------------------- bits
class _Reader:
    """LSB-first bits of data[start:end]; past `end` it reads zeros, as the game does, and `overrun` says so."""
    __slots__ = ("d", "p", "end", "start", "acc", "n", "used")

    def __init__(self, d, start, end):
        self.d, self.p, self.start, self.end = d, start, start, end
        self.acc = self.n = self.used = 0

    def fill(self, want):
        while self.n < want:
            take = min(8, self.end - self.p)
            if take > 0:
                self.acc |= int.from_bytes(self.d[self.p:self.p + take], "little") << self.n
                self.p += take
                self.n += 8 * take
            else:
                self.n += 64  # zeros

    def bit(self):
        if self.n < 1:
            self.fill(1)
        v = self.acc & 1
        self.acc >>= 1
        self.n -= 1
        self.used += 1
        return v

    def get(self, k):
        if self.n < k:
            self.fill(k)
        v = self.acc & ((1 << k) - 1)
        self.acc >>= k
        self.n -= k
        self.used += k
        return v

    def gamma(self, limit, what):
        if self.n < 40:
            self.fill(40)
        acc, v, used = self.acc, 1, 1
        while acc & 1:
            v = (v << 1) | ((acc >> 1) & 1)
            acc >>= 2
            used += 2
            if v > limit:
                raise TileFormatError(f"{what}: a run longer than {limit}")
        self.acc = acc >> 1
        self.n -= used
        self.used += used
        return v

    @property
    def overrun(self):
        return self.used > 8 * (self.end - self.start)

    @property
    def bytes_used(self):
        return (self.used + 7) // 8


class _Writer:
    __slots__ = ("acc", "n")

    def __init__(self):
        self.acc = self.n = 0

    def put(self, v, k):
        self.acc |= v << self.n
        self.n += k

    def gamma(self, v):
        if v < 1:
            raise EncodeError("a run of 0")
        code, bits = _GAMMA.get(v) or _gamma_code(v)
        self.acc |= code << self.n
        self.n += bits

    def to_bytes(self):
        return self.acc.to_bytes((self.n + 7) // 8, "little")


_GAMMA = {}


def _gamma_code(v):
    code = pos = 0
    for i in range(v.bit_length() - 2, -1, -1):
        code |= (1 | (((v >> i) & 1) << 1)) << pos
        pos += 2
    r = (code, pos + 1)  # the closing 0
    if v <= 4096:
        _GAMMA[v] = r
    return r


def gamma_bits(v):
    return 2 * (v.bit_length() - 1) + 1


# ---------------------------------------------------------------- 8x8x8 block codec
def _decode_block(data, stats=None):
    n = len(data)
    if n == 0 or n > MAX_BLOCK_BYTES:
        raise TileFormatError(f"block: {n} bytes (1 to {MAX_BLOCK_BYTES})")
    r = _Reader(data, 0, n)
    tail = n
    mats = bytearray(BLOCK_CELLS)
    i = prev = older = 0
    runs = 0
    while i < BLOCK_CELLS:
        if r.bit():
            tail -= 1
            if tail < 0:
                raise TileFormatError("block: material literals past the front")
            m = data[tail]
        else:
            m = older
        c = r.gamma(BLOCK_CELLS, "block materials")
        if i + c > BLOCK_CELLS:
            raise TileFormatError("block: material runs past 512 cells")
        mats[i:i + c] = bytes((m,)) * c
        i += c
        older, prev = prev, m
        runs += 1
    fills = bytearray(BLOCK_CELLS)
    i = bf = 0
    while i < BLOCK_CELLS:
        m = mats[i]
        if r.bit():
            tail -= 1
            if tail < 0:
                raise TileFormatError("block: fill literals past the front")
            f = data[tail]
            if m >= 128:
                bf = f
            c = 1 if (m < 128 and 0 < f < 255) else r.gamma(BLOCK_CELLS, "block fills")
        else:
            f = 0 if m == 0 else (255 if m < 128 else bf)
            c = r.gamma(BLOCK_CELLS, "block fills")
        if i + c > BLOCK_CELLS:
            raise TileFormatError("block: fill runs past 512 cells")
        fills[i:i + c] = bytes((f,)) * c
        i += c
    if r.overrun or r.bytes_used != tail:
        raise TileFormatError(f"block: {r.bytes_used} control bytes and {n - tail} literals in {n} bytes")
    if stats is not None:
        stats["literals"] = n - tail
        stats["material_runs"] = runs
    return mats, fills


def decode_block(data):
    """A compressed block -> 512 cells (array of u16), or TileFormatError."""
    mats, fills = _decode_block(data)
    buf = bytearray(2 * BLOCK_CELLS)
    buf[0::2] = mats
    buf[1::2] = fills
    out = array("H")
    out.frombytes(bytes(buf))
    if sys.byteorder == "big":  # pragma: no cover
        out.byteswap()
    return out


def block_literals(data):
    """How many literal bytes a compressed block holds (its tail)."""
    s = {}
    _decode_block(data, s)
    return s["literals"]


def _split(cells):
    if len(cells) != BLOCK_CELLS:
        raise EncodeError(f"a block is 512 cells, not {len(cells)}")
    try:
        a = array("H", cells)
    except OverflowError:
        raise EncodeError("a cell is a u16 (0..0xFFFF)") from None
    if sys.byteorder == "big":  # pragma: no cover
        a.byteswap()
    b = a.tobytes()
    return b[0::2], b[1::2]


def _runs(seq, start):
    v = seq[start]
    j = start + 1
    n = len(seq)
    while j < n and seq[j] == v:
        j += 1
    return j


def encode_block(cells):
    """512 cells -> a compressed block, as short as this writer makes it (EncodeError past 1,023 bytes)."""
    mats, fills = _split(cells)
    w = _Writer()
    lits = bytearray()
    i = prev = older = 0
    while i < BLOCK_CELLS:
        m = mats[i]
        j = _runs(mats, i)
        if m == older:
            w.put(0, 1)
        else:
            w.put(1, 1)
            lits.append(m)
        w.gamma(j - i)
        older, prev = prev, m
        i = j
    i = bf = 0
    while i < BLOCK_CELLS:
        m, f = mats[i], fills[i]
        d = 0 if m == 0 else (255 if m < 128 else bf)
        if f == d:
            w.put(0, 1)
            j = _runs(fills, i)
            w.gamma(j - i)
        else:
            w.put(1, 1)
            lits.append(f)
            if m >= 128:
                bf = f
            if m < 128 and 0 < f < 255:
                j = i + 1
            else:
                j = _runs(fills, i)
                w.gamma(j - i)
        i = j
    out = w.to_bytes() + bytes(reversed(lits))
    if len(out) > MAX_BLOCK_BYTES:
        raise EncodeError(f"the block packs to {len(out)} bytes; a block holds at most {MAX_BLOCK_BYTES}")
    return out


# ---------------------------------------------------------------- columns
class Column:
    """One block-column as its runs: ("u", count, material | None, fill | None) or ("c", [block bytes, ...]).
    A uniform run's material None = "the last uniform value"; fill None = no fill byte."""
    __slots__ = ("runs", "pad_bits")

    def __init__(self, runs, pad_bits=0):
        self.runs = runs
        self.pad_bits = pad_bits   # the value of the unused bits of the last control byte (0 in a canonical column)

    @property
    def height(self):
        return sum(r[1] if r[0] == "u" else len(r[1]) for r in self.runs)

    def blocks(self):
        """Every block from y = 0 up: an int (a uniform block's cell value) or a 512-cell array."""
        out = []
        mat, fill = 0, None
        for r in self.runs:
            if r[0] == "u":
                _, count, m, f = r
                if m is not None:
                    mat, fill = m, f
                v = mat | ((fill if fill is not None else uniform_fill(mat)) << 8)
                out.extend([v] * count)
            else:
                out.extend(decode_block(b) for b in r[1])
        return out

    def compressed(self):
        """Indices of the blocks this column stores compressed (the game also compresses some one-value blocks)."""
        out, y = set(), 0
        for r in self.runs:
            if r[0] == "u":
                y += r[1]
            else:
                out.update(range(y, y + len(r[1])))
                y += len(r[1])
        return out

    def to_bytes(self):
        w = _Writer()
        payload = bytearray()
        for r in self.runs:
            if r[0] == "u":
                _, count, m, f = r
                w.gamma(count)
                w.put(0, 1)
                if m is None:
                    w.put(0, 1)
                else:
                    w.put(1, 1)
                    w.put(0 if f is None else 1, 1)
                    payload.append(m)
                    if f is not None:
                        payload.append(f)
            else:
                blocks = r[1]
                w.gamma(len(blocks))
                w.put(1, 1)
                for b in blocks:
                    w.put(len(b), 10)
                    payload += b
        if self.pad_bits:
            w.put(self.pad_bits, (8 - w.n % 8) % 8)
        ctrl = w.to_bytes()
        h = self.height
        if h > MAX_HEIGHT or len(ctrl) > MAX_CONTROL_BYTES:
            raise EncodeError(f"a column of {h} blocks with {len(ctrl)} control bytes does not fit its u16 fields")
        return struct.pack("<HH", h, len(ctrl)) + ctrl + bytes(payload)


def parse_column(data, decode=False, stats=None):
    """A column's bytes (exactly one column) -> Column; TileFormatError for anything the game would misread.
    decode=True also decodes every compressed block."""
    n = len(data)
    if n < 4:
        raise TileFormatError(f"column: {n} bytes, shorter than its 4-byte header")
    height, ctrl = struct.unpack_from("<HH", data)
    if 4 + ctrl > n:
        raise TileFormatError(f"column: {ctrl} control bytes in {n - 4}")
    r = _Reader(data, 4, 4 + ctrl)
    at = 4 + ctrl
    runs = []
    done = 0
    while done < height:
        count = r.gamma(MAX_COLUMN_RUN, "column")
        if done + count > height:
            raise TileFormatError(f"column: runs past its height {height}")
        if not r.bit():
            if r.bit():
                has_fill = r.bit()
                if at + 1 + has_fill > n:
                    raise TileFormatError("column: a uniform value past the payload")
                m = data[at]
                f = data[at + 1] if has_fill else None
                at += 1 + has_fill
                runs.append(("u", count, m, f))
            else:
                runs.append(("u", count, None, None))
        else:
            blocks = []
            for _ in range(count):
                size = r.get(10)
                if size == 0 or at + size > n:
                    raise TileFormatError(f"column: a compressed block of {size} bytes past the payload")
                blob = bytes(data[at:at + size])
                if decode:
                    _decode_block(blob)
                blocks.append(blob)
                at += size
            runs.append(("c", blocks))
        done += count
    if r.overrun or r.bytes_used != ctrl:
        raise TileFormatError(f"column: its runs use {r.bytes_used} of {ctrl} control bytes")
    if at != n:
        raise TileFormatError(f"column: {n - at} payload bytes left over")
    pad = r.acc & ((1 << ((8 - r.used % 8) % 8)) - 1)
    if stats is not None:
        stats["blocks"] = stats.get("blocks", 0) + height
        for run in runs:
            if run[0] == "u":
                stats["uniform"] = stats.get("uniform", 0) + run[1]
                stats["uniform_runs"] = stats.get("uniform_runs", 0) + 1
                if run[2] is not None and run[2] >= 128 and run[3] is None:
                    # a building material with the derived fill: what the reader derives is not proven for these
                    stats["building_runs_without_fill"] = stats.get("building_runs_without_fill", 0) + 1
            else:
                stats["compressed"] = stats.get("compressed", 0) + len(run[1])
                stats["compressed_runs"] = stats.get("compressed_runs", 0) + 1
        if pad:
            stats["padded_columns"] = stats.get("padded_columns", 0) + 1
    return Column(runs, pad)


def _uniform_value(b):
    if isinstance(b, int):
        if not 0 <= b <= 0xFFFF:
            raise EncodeError("a cell is a u16 (0..0xFFFF)")
        return b
    if len(b) != BLOCK_CELLS:
        raise EncodeError(f"a block is 512 cells, not {len(b)}")
    first = b[0]
    if b.count(first) != BLOCK_CELLS:
        return None
    if not 0 <= first <= 0xFFFF:
        raise EncodeError("a cell is a u16 (0..0xFFFF)")
    return first


def column_runs(blocks, compressed=()):
    """Blocks (ints for uniform blocks, 512-cell sequences) -> the runs this writer chooses: a 512-cell block that is one
    value becomes uniform unless its index is in `compressed`; equal uniform blocks share a run; a uniform value equal
    to the column's last uniform value (at first air) is written as "same as before"; a fill byte only where the fill
    is not the uniform default (always for a building material). The game's own tiles follow the same rules, except that they store some one-value
    blocks (mostly air at a column's top) compressed; `compressed` (Column.compressed()) keeps that choice, and then a
    column of the game is rebuilt byte for byte."""
    runs = []
    mat, fill = 0, 0          # the column's last uniform value: air at first
    i, n = 0, len(blocks)
    vals = [_uniform_value(b) for b in blocks]
    forced = [j for j in compressed if 0 <= j < n and vals[j] is not None]
    if forced:
        blocks = list(blocks)
        for j in forced:
            if isinstance(blocks[j], int):
                blocks[j] = [blocks[j]] * BLOCK_CELLS
            vals[j] = None
    while i < n:
        v = vals[i]
        if v is not None:
            j = i + 1
            while j < n and vals[j] == v and j - i < MAX_COLUMN_RUN:
                j += 1
            m, f = v & 0xFF, v >> 8
            if m == mat and f == fill:
                runs.append(("u", j - i, None, None))
            else:
                # a building material always gets its fill byte: what the reader derives without one is only known for
                # terrain (0 for air, 255 otherwise; the dev.28 lab's live column), and no game tile needs it
                runs.append(("u", j - i, m, None if f == uniform_fill(m) and m < 128 else f))
                mat, fill = m, f
        else:
            j = i
            out = []
            while j < n and vals[j] is None and j - i < MAX_COLUMN_RUN:
                out.append(encode_block(blocks[j]))
                j += 1
            runs.append(("c", out))
        i = j
    return runs


def encode_column(blocks, compressed=()):
    """Blocks from y = 0 up (ints for uniform blocks, or 512-cell sequences) -> one column's bytes. `compressed`: block
    indices to store compressed even where the block is one value (see column_runs)."""
    return Column(column_runs(blocks, compressed)).to_bytes()


# ---------------------------------------------------------------- tiles
class Tile:
    __slots__ = ("blocks_x", "blocks_z", "columns")

    def __init__(self, blocks_x, blocks_z, columns):
        self.blocks_x, self.blocks_z, self.columns = blocks_x, blocks_z, columns

    @property
    def header_size(self):
        return 2 + 3 * self.blocks_x * self.blocks_z

    def column(self, bx, bz):
        return self.columns[bz * self.blocks_x + bx]

    def to_bytes(self):
        return build_tile(self.blocks_x, self.blocks_z, self.columns)


def build_tile(blocks_x, blocks_z, columns):
    """Columns' bytes (index bz * blocks_x + bx) -> a tile."""
    if not (1 <= blocks_x <= 255 and 1 <= blocks_z <= 255):
        raise EncodeError(f"tile dimensions {blocks_x} x {blocks_z} (1..255 block-columns each)")
    if len(columns) != blocks_x * blocks_z:
        raise EncodeError(f"{len(columns)} columns for a {blocks_x} x {blocks_z} tile")
    head = bytearray((blocks_x, blocks_z))
    at = 0
    for c in columns:
        if at > MAX_OFFSET:
            raise EncodeError("a tile's columns start past its u24 offsets")
        head += at.to_bytes(3, "little")
        at += len(c)
    return bytes(head) + b"".join(columns)


def empty_tile(blocks_x, blocks_z):
    """Every column empty (height 0): the tile the dev.28 lab served (32 x 32: 7,170 bytes)."""
    return build_tile(blocks_x, blocks_z, [b"\0\0\0\0"] * (blocks_x * blocks_z))


def parse_tile(data):
    """A tile blob -> Tile (columns as bytes, not yet parsed); TileFormatError for a bad envelope."""
    n = len(data)
    if n < 2:
        raise TileFormatError(f"tile: {n} bytes")
    bx, bz = data[0], data[1]
    count = bx * bz
    if count == 0:
        raise TileFormatError(f"tile: {bx} x {bz} block-columns")
    head = 2 + 3 * count
    if head > n:
        raise TileFormatError(f"tile: its {count} offsets need {head} bytes, it has {n}")
    payload = n - head
    offs = [int.from_bytes(data[2 + 3 * i:5 + 3 * i], "little") for i in range(count)]
    if offs[0] != 0:
        raise TileFormatError(f"tile: the first column starts at {offs[0]}, not 0")
    offs.append(payload)
    cols = []
    for i in range(count):
        a, b = offs[i], offs[i + 1]
        if b < a or b > payload:
            raise TileFormatError(f"tile: column {i} spans {a}..{b} of a {payload}-byte payload")
        cols.append(bytes(data[head + a:head + b]))
    return Tile(bx, bz, cols)


def check_tile(data, decode=True):
    """Every rule the reader relies on, over the whole tile (envelope, every column's runs and payload, and with
    decode=True every compressed block). Returns counts; raises TileFormatError naming the column."""
    t = parse_tile(data)
    s = {"bytes": len(data), "blocks_x": t.blocks_x, "blocks_z": t.blocks_z, "columns": len(t.columns), "blocks": 0,
         "uniform": 0, "compressed": 0, "max_height": 0, "empty_columns": 0}
    for i, c in enumerate(t.columns):
        if not c:
            s["empty_columns"] += 1   # the reader gives height 0 for a zero-length column
            continue
        try:
            col = parse_column(c, decode=decode, stats=s)
        except TileFormatError as e:
            raise TileFormatError(f"column {i} (block-column {i % t.blocks_x}, {i // t.blocks_x}): {e}") from None
        s["max_height"] = max(s["max_height"], col.height)
    return s


class TileCells:
    """A tile's cells to read and change. Columns not changed keep their bytes when written back; in a changed column
    the blocks not changed keep the game's choice of uniform or compressed (Column.compressed)."""

    def __init__(self, blocks_x, blocks_z, columns, raw=None):
        self.blocks_x, self.blocks_z = blocks_x, blocks_z
        self._cols = columns            # per column: list of blocks (int or array) or None (not decoded yet)
        self._raw = raw                 # per column: its original bytes
        self._kept = {}                 # per decoded column: the blocks it stored compressed
        self._dirty = set()
        self._touched = {}              # per changed column: the blocks changed

    @classmethod
    def empty(cls, blocks_x, blocks_z):
        n = blocks_x * blocks_z
        return cls(blocks_x, blocks_z, [[] for _ in range(n)], [b"\0\0\0\0"] * n)

    @classmethod
    def from_bytes(cls, data):
        t = parse_tile(data)
        return cls(t.blocks_x, t.blocks_z, [None] * len(t.columns), t.columns)

    @property
    def cells_x(self):
        return self.blocks_x * BLOCK_EDGE

    @property
    def cells_z(self):
        return self.blocks_z * BLOCK_EDGE

    def _index(self, x, z):
        if not (0 <= x < self.cells_x and 0 <= z < self.cells_z):
            raise IndexError(f"cell ({x}, {z}) outside this {self.cells_x} x {self.cells_z} tile")
        return (z >> 3) * self.blocks_x + (x >> 3)

    def blocks(self, i):
        c = self._cols[i]
        if c is None:
            raw = self._raw[i]
            col = parse_column(raw) if raw else Column([])
            c = self._cols[i] = col.blocks()
            self._kept[i] = col.compressed()
        return c

    def column_height(self, bx, bz):
        return len(self.blocks(bz * self.blocks_x + bx))

    def get(self, x, y, z):
        blocks = self.blocks(self._index(x, z))
        b = y >> 3
        if y < 0 or b >= len(blocks):
            return 0
        v = blocks[b]
        return v if isinstance(v, int) else v[((z & 7) * 8 + (y & 7)) * 8 + (x & 7)]

    def column_cells(self, x, z):
        """Cells of one cell-column from y = 0 to the top of its block-column."""
        blocks = self.blocks(self._index(x, z))
        k = (z & 7) * 64 + (x & 7)
        out = []
        for v in blocks:
            out.extend([v] * 8 if isinstance(v, int) else [v[k + 8 * y] for y in range(8)])
        return out

    def set(self, x, y, z, value):
        if not 0 <= value <= 0xFFFF:
            raise EncodeError("a cell is a u16 (0..0xFFFF)")
        if y < 0 or (y >> 3) >= MAX_HEIGHT:
            raise IndexError(f"cell height {y} outside 0..{8 * MAX_HEIGHT - 1}")
        i = self._index(x, z)
        blocks = self.blocks(i)
        b = y >> 3
        k = ((z & 7) * 8 + (y & 7)) * 8 + (x & 7)
        v = blocks[b] if b < len(blocks) else 0
        if (v if isinstance(v, int) else v[k]) == value:
            return
        while len(blocks) <= b:
            blocks.append(0)
        if isinstance(v, int):
            v = blocks[b] = array("H", [v]) * BLOCK_CELLS
        v[k] = value
        self._dirty.add(i)
        self._touched.setdefault(i, set()).add(b)

    @property
    def changed_columns(self):
        return sorted(self._dirty)

    def to_bytes(self):
        cols = []
        for i in range(self.blocks_x * self.blocks_z):
            if i in self._dirty or self._raw is None:
                blocks = self.blocks(i)
                keep = self._kept.get(i, set()) - self._touched.get(i, set())
                cols.append(encode_column(blocks, keep))
            else:
                cols.append(self._raw[i])
        return build_tile(self.blocks_x, self.blocks_z, cols)
