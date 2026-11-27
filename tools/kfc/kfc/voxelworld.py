# SPDX-License-Identifier: MIT
"""keen::VoxelWorldResource: the root of a baked voxel world and its tile references, read and patched (clean-room).

The main world (GUID 509feadb-4c60-425f-9c7c-deeefd9b6920) has two: part 0 solid ground, part 1 the Shroud's fog.
Reflected layout of server 26430253 / client 26430625 (layout hash 0xce3a352f; dossier chapter 19f section 2.1):

    +0x0000 u8 type (0 Solid, 1 Fog, 3 Temperature)   +0x0004 uint3 size     +0x0010 int3 origin
    +0x001c Guid materialGuids[256]                    +0x101c u8 defaultTerrainMaterial
    +0x1020 ResourceContentReference cpuDisplacement   +0x1040 VoxelWorldLevel voxelLevels[7] (24 bytes each)
    +0x10e8 ResourceContentReference voxelHashes       (4,360 bytes; the tile arrays follow)

    VoxelWorldLevel: uint2 tileSize (cells) +0, uint2 tileCount +8, BlobArray<ResourceContentReference> tiles +0x10
    ResourceContentReference (32 bytes): ContentHash hash +0, HashKey32 category +0x10, u32 bundleId +0x14, u64 sortKey +0x18
    BlobArray: {i32 offset relative to the field itself, u32 count}

Tile i of a level is tile (i % tileCount.x, i // tileCount.x). Level 6 has 0.5 m cells, each coarser level doubles
them (level 0: 32 m); `size` is in level-5 cells (metres), so a level has size * 2^level / 32 cells per axis. A tile
covers tileSize cells, the last tile of a row or column what is left (so level 0's edge tiles have 8 block-columns
across where the others have 16): the tile's own two dimension bytes must say the same.

A tile reference's hash is the game's own ContentHash of the tile's bytes (kfc.contenthash), which is also its key in
the game's content directory; patching a reference writes another such key (see `patched`).
"""
import struct

from .archive import ContentHash, ResourceKey
from .util import fnv1a32, guid_bytes, guid_text

TYPE_NAME = "keen::VoxelWorldResource"
TYPE_HASH = fnv1a32(TYPE_NAME)           # 0x75f48072
LAYOUT_HASH = 0xCE3A352F                 # the reflected layout this module reads (server 26430253, client 26430625)
MAIN_WORLD = "509feadb-4c60-425f-9c7c-deeefd9b6920"
SOLID, FOG = 0, 1
TYPES = {0: "solid", 1: "fog", 3: "temperature"}

HEADER_SIZE = 4360
LEVELS = 7
FINEST = 6
LEVELS_AT = 0x1040
LEVEL_STRIDE = 24
CPU_DISPLACEMENT_AT = 0x1020
VOXEL_HASHES_AT = 0x10E8
REF_SIZE = 32


class WorldFormatError(ValueError):
    """A root this module does not read (another layout, a field that disagrees with the rest): refused."""


def cell_size(level):
    """Metres per cell at a level (level 6: 0.5 m)."""
    return 0.5 * (1 << (FINEST - level))


class Ref:
    """One ResourceContentReference: its ContentHash, the rest of its 32 bytes, and where its hash sits in the root."""
    __slots__ = ("hash", "category", "bundle", "sort_key", "offset")

    def __init__(self, raw, offset):
        self.hash = ContentHash(raw[offset:offset + 16])
        self.category, self.bundle, self.sort_key = struct.unpack_from("<IIQ", raw, offset + 16)
        self.offset = offset

    def __repr__(self):
        return f"Ref({self.hash}, at {self.offset})"


class Level:
    __slots__ = ("index", "tile_size", "tile_count", "tiles", "cells")

    def __init__(self, index, tile_size, tile_count, tiles, cells):
        self.index, self.tile_size, self.tile_count, self.tiles, self.cells = index, tile_size, tile_count, tiles, cells

    @property
    def cell_m(self):
        return cell_size(self.index)

    def tile_index(self, tx, tz):
        if not (0 <= tx < self.tile_count[0] and 0 <= tz < self.tile_count[1]):
            raise IndexError(f"tile ({tx}, {tz}) outside level {self.index}'s {self.tile_count[0]} x {self.tile_count[1]}")
        return tz * self.tile_count[0] + tx

    def tile_xz(self, index):
        return index % self.tile_count[0], index // self.tile_count[0]

    def tile_cells(self, tx, tz):
        """The cells the tile covers per axis (x, z): tileSize, or what is left at the world's far edge."""
        return (min(self.tile_size[0], self.cells[0] - tx * self.tile_size[0]),
                min(self.tile_size[1], self.cells[2] - tz * self.tile_size[1]))

    def tile_blocks(self, tx, tz):
        """The block-columns the tile's own dimension bytes must hold."""
        x, z = self.tile_cells(tx, tz)
        return (x + 7) // 8, (z + 7) // 8


class VoxelWorld:
    """A parsed VoxelWorldResource. `raw` stays as read: `patched` copies it with other tile hashes."""

    def __init__(self, raw):
        self.raw = bytes(raw)
        n = len(self.raw)
        if n < HEADER_SIZE:
            raise WorldFormatError(f"VoxelWorldResource: {n} bytes, shorter than its {HEADER_SIZE}-byte header")
        b = self.raw
        self.type = b[0]
        self.size = struct.unpack_from("<3I", b, 4)
        self.origin = struct.unpack_from("<3i", b, 0x10)
        self.material_guids = [b[0x1C + 16 * i:0x2C + 16 * i] for i in range(256)]
        self.default_material = b[0x101C]
        self.cpu_displacement = Ref(b, CPU_DISPLACEMENT_AT)
        self.voxel_hashes = Ref(b, VOXEL_HASHES_AT)
        self.levels = []
        for li in range(LEVELS):
            at = LEVELS_AT + LEVEL_STRIDE * li
            tsx, tsz, tcx, tcz = struct.unpack_from("<4I", b, at)
            rel, count = struct.unpack_from("<iI", b, at + 16)
            first = at + 16 + rel
            if count != tcx * tcz:
                raise WorldFormatError(f"level {li}: {count} tile references for a {tcx} x {tcz} grid")
            if first < HEADER_SIZE or first + REF_SIZE * count > n:
                raise WorldFormatError(f"level {li}: its tile array {first}..{first + REF_SIZE * count} is outside {n} bytes")
            cells = tuple(s * (1 << li) // 32 for s in self.size)
            if not tsx or not tsz or tcx != -(-cells[0] // tsx) or tcz != -(-cells[2] // tsz) or tsx % 8 or tsz % 8:
                raise WorldFormatError(f"level {li}: {tcx} x {tcz} tiles of {tsx} x {tsz} cells do not cover "
                                       f"{cells[0]} x {cells[2]} cells")
            tiles = [Ref(b, first + REF_SIZE * i) for i in range(count)]
            self.levels.append(Level(li, (tsx, tsz), (tcx, tcz), tiles, cells))

    @property
    def kind(self):
        return TYPES.get(self.type, f"type {self.type}")

    def material_guid(self, material):
        return guid_text(self.material_guids[material])

    def refs(self):
        """Every tile reference: (level, index, Ref)."""
        for lv in self.levels:
            for i, r in enumerate(lv.tiles):
                yield lv.index, i, r

    def tile_of_cell(self, x, z, level=FINEST):
        """(tile index, local x, local z) of a world cell at `level`."""
        lv = self.levels[level]
        tx, tz = x // lv.tile_size[0], z // lv.tile_size[1]
        return lv.tile_index(tx, tz), x - tx * lv.tile_size[0], z - tz * lv.tile_size[1]

    def patched(self, hashes):
        """A copy of the root with tile references pointing at other content: {(level, index): ContentHash}. Only the
        16-byte hash of each reference changes (category, bundle and sort key stay); returns the bytes."""
        out = bytearray(self.raw)
        for (level, index), h in hashes.items():
            if not isinstance(h, ContentHash):
                h = ContentHash(h)
            r = self.levels[level].tiles[index]
            out[r.offset:r.offset + 16] = h.raw()
        return bytes(out)


def key(part, guid=MAIN_WORLD):
    return ResourceKey(guid_bytes(guid), TYPE_HASH, part)


def layout_of(kfc):
    """The archive's layout hash for VoxelWorldResource (None if it has none)."""
    b = kfc.bundles.get(TYPE_HASH)
    return b["layout"] if b else None


def load(kfc, part=SOLID, guid=MAIN_WORLD, check_layout=True):
    """The root of `guid` / `part` from a Kfc. Refuses another reflected layout than the one this module reads."""
    if check_layout:
        lay = layout_of(kfc)
        if lay != LAYOUT_HASH:
            raise WorldFormatError(f"VoxelWorldResource layout {lay if lay is None else hex(lay)} in {kfc.path}; this "
                                   f"reader knows {LAYOUT_HASH:#x} (server 26430253, client 26430625): re-check the layout")
    return VoxelWorld(kfc.read(key(part, guid)))
