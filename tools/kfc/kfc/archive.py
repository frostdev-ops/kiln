# SPDX-License-Identifier: MIT
"""KFC4 reader: enshrouded(.kfc|_server.kfc) + .kfc_resources + _NNN.dat. Read-only by design.

Layout (Kiln's own reading of the 1285601 files; EMF re_content.md 1.1 gives the outline). Little-endian. A
"location" is {u32 offset relative to the location's own position, u32 count}.

    0x00 "KFC4"   0x04 u32 end of the tables   0x08 u32 12   0x0C u32 0
    0x10 version string (bytes)       0x18 containers {u64 size, u64 content count}
    0x20, 0x28 unused (empty)         0x30 resource stream {u32 raw size, u32 compressed size, u32 resource
                                           count, u32 0, u32 chunk count} (one entry)
    0x38 resource order (u32 index per resource; the bundles index this list)
    0x40 content buckets {u32 first, u32 count}   0x48 content keys ContentHash {u32 size, u32 h0, h1, h2}
    0x50 content values {u32 offset, u16 flags, u16 container, u64 0}
    0x58 resource buckets             0x60 resource keys {guid[16], u32 type hash, u32 part, u64 0}
    0x68 resource values {u32 offset in the decompressed stream, u32 size}
    0x70 bundle buckets               0x78 bundle keys (u32 type hash)
    0x80 bundle values {u32 type layout hash, u32 first (into the resource order), u32 count}
    0x88 resource chunks {u32 file offset, u32 stored size (4 KiB aligned), u32 compressed size,
                          u32 decompressed offset, u32 decompressed size}: zstd frames of 8 MiB

The static-map buckets are not needed for reading: the key and value arrays are parallel, so a reader builds its
own dictionaries. Contents live in `<stem>_<container:03d>.dat` at `offset` (4 KiB aligned), `size` bytes.
"""
import struct
from collections import OrderedDict
from pathlib import Path

from .util import guid_bytes, guid_text

_zstandard = None
try:
    from compression import zstd as _zstd  # Python 3.14+
except ImportError:  # pragma: no cover
    _zstd = None
    try:
        import zstandard as _zstandard
    except ImportError:
        _zstandard = None

MAGIC = b"KFC4"

_ZSTD_UNKNOWN = 0xFFFFFFFFFFFFFFFF      # ZSTD_CONTENTSIZE_UNKNOWN
_ZSTD_NOT_A_FRAME = 0xFFFFFFFFFFFFFFFE  # ZSTD_CONTENTSIZE_ERROR
_ZSTD_MAX_OUT = 1 << 30                 # no resource of the game is near this; a broken size field must not allocate more
_libzstd = None                         # None: not looked for yet; False: looked for, not there


def _system_libzstd():
    """The system's libzstd through ctypes, or False: for a Python older than 3.14 without the zstandard package
    (most Linux installs have the library itself, because the system's own tools use it)."""
    global _libzstd
    if _libzstd is None:
        _libzstd = False
        try:
            import ctypes
            import ctypes.util
        except ImportError:  # pragma: no cover
            return _libzstd
        found = ctypes.util.find_library("zstd")
        for name in ([found] if found else []) + ["libzstd.so.1", "libzstd.so", "libzstd.1.dylib", "libzstd.dylib", "libzstd.dll", "zstd.dll"]:
            try:
                lib = ctypes.CDLL(name)
                lib.ZSTD_getFrameContentSize.restype = ctypes.c_ulonglong
                lib.ZSTD_getFrameContentSize.argtypes = [ctypes.c_char_p, ctypes.c_size_t]
                lib.ZSTD_decompress.restype = ctypes.c_size_t
                lib.ZSTD_decompress.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_size_t]
                lib.ZSTD_isError.restype = ctypes.c_uint
                lib.ZSTD_isError.argtypes = [ctypes.c_size_t]
            except (OSError, AttributeError):
                continue
            _libzstd = lib
            break
    return _libzstd


def _libzstd_decompress(lib, data, size_hint):
    import ctypes
    data = bytes(data)
    size = lib.ZSTD_getFrameContentSize(data, len(data))
    if size == _ZSTD_NOT_A_FRAME:
        raise RuntimeError("zstd: not a zstd frame")
    if size == _ZSTD_UNKNOWN:
        size = size_hint or (64 << 20)
    if size > _ZSTD_MAX_OUT:
        raise RuntimeError(f"zstd: the frame says {size} bytes, more than any game resource holds")
    out = ctypes.create_string_buffer(max(size, 1))
    n = lib.ZSTD_decompress(out, size, data, len(data))
    if lib.ZSTD_isError(n):
        raise RuntimeError("zstd: the data does not decompress (damaged, or larger than expected)")
    return out.raw[:n]


def zstd_backend():
    """Which zstd decoder this Python has: "stdlib" (3.14+), "zstandard" (the package), "libzstd" (the system's
    library through ctypes), or None."""
    if _zstd is not None:
        return "stdlib"
    if _zstandard is not None:
        return "zstandard"
    return "libzstd" if _system_libzstd() else None


def zstd_decompress(data, size_hint=0):
    if _zstd is not None:
        return _zstd.decompress(data)
    if _zstandard is not None:  # pragma: no cover
        return _zstandard.ZstdDecompressor().decompress(data, max_output_size=size_hint or (64 << 20))
    lib = _system_libzstd()
    if lib:
        return _libzstd_decompress(lib, data, size_hint)
    raise RuntimeError("no zstd decoder: use Python 3.14 or newer, or install the 'zstandard' package or the system's zstd library (libzstd)")


class ResourceKey(tuple):
    """(guid bytes, type hash, part)."""
    __slots__ = ()

    def __new__(cls, guid, type_hash, part=0):
        if isinstance(guid, str):
            guid = guid_bytes(guid)
        return tuple.__new__(cls, (bytes(guid), type_hash, part))

    @property
    def guid(self):
        return guid_text(self[0])

    def __repr__(self):
        return f"{self.guid}:{self[1]:08x}:{self[2]}"


class ContentHash(tuple):
    """(size, h0, h1, h2): the 16-byte keen::ContentHash. Text forms: 32 hex digits of the 16 bytes, or kiln.assets'
    "<size>-<h0>-<h1>-<h2>" (hex numbers)."""
    __slots__ = ()

    def __new__(cls, size, h0=None, h1=None, h2=None):
        if isinstance(size, (bytes, bytearray)):
            return tuple.__new__(cls, struct.unpack("<4I", size[:16]))
        if isinstance(size, str):
            if "-" in size:  # kiln.assets' spelling: <size>-<h0>-<h1>-<h2> in hex
                return tuple.__new__(cls, tuple(int(x, 16) for x in size.split("-")))
            return tuple.__new__(cls, struct.unpack("<4I", bytes.fromhex(size)))
        if isinstance(size, dict):
            return tuple.__new__(cls, (size["size"], size["hash0"], size["hash1"], size["hash2"]))
        return tuple.__new__(cls, (size, h0, h1, h2))

    @property
    def size(self):
        return self[0]

    def raw(self):
        return struct.pack("<4I", *self)

    def __str__(self):
        return self.raw().hex()

    def assets_text(self):
        """kiln.assets' spelling (dump specs, assets.json): <size>-<h0>-<h1>-<h2> in hex."""
        return "-".join(f"{x:x}" for x in self)

    __repr__ = __str__


class Kfc:
    def __init__(self, path, chunk_cache=6, stem=None, resources=None):
        """`path`: the index file, or a folder holding one `*.kfc`. `stem` and `resources` name the other files when
        the index is not `<stem>.kfc` beside them (see `unpatched`): `<stem>_NNN.dat`, and the resources file."""
        p = Path(path).expanduser()
        if p.is_dir():
            cands = sorted(p.glob("*.kfc"))
            if not cands:
                raise FileNotFoundError(f"no .kfc in {p}")
            p = cands[0]
        self.path = p
        self.stem = Path(stem) if stem is not None else p.with_suffix("")
        self._resources = Path(resources) if resources is not None else Path(str(self.stem) + ".kfc_resources")
        self.b = p.read_bytes()
        if self.b[:4] != MAGIC:
            raise ValueError(f"{p}: magic {self.b[:4]!r}, expected {MAGIC!r}")
        self._parse()
        self._res_file = None
        self._chunks_cache = OrderedDict()
        self._chunk_cache_max = chunk_cache
        self._dat_files = {}

    @classmethod
    def unpatched(cls, path, chunk_cache=6):
        """The game's own archive in a folder whose archive a mod loader patched, or None when it is as the game
        shipped it. EML rewrites `<name>.kfc` and `<name>.kfc_resources` for its mods and keeps the originals beside
        them as `<name>.kfc.bak` and `<name>.kfc_resources.bak`; the `.dat` files only grow, so the originals' entries
        still read. Anything that states a fact about the game's data (a rule that holds for every game material)
        has to read this one: in the patched archive a mod's resources stand where the game's were."""
        p = Path(path).expanduser()
        if p.is_dir():
            cands = sorted(p.glob("*.kfc"))
            if not cands:
                return None
            p = cands[0]
        stem = p.with_suffix("")
        index, resources = Path(str(p) + ".bak"), Path(str(stem) + ".kfc_resources.bak")
        if not (index.is_file() and resources.is_file()):
            return None
        return cls(index, chunk_cache=chunk_cache, stem=stem, resources=resources)

    # ---- header ----
    def _loc(self, at):
        rel, count = struct.unpack_from("<II", self.b, at)
        return at + rel, count

    def _parse(self):
        b = self.b
        self.tables_end = struct.unpack_from("<I", b, 4)[0]
        o, n = self._loc(0x10)
        self.version = b[o:o + n].decode("utf-8", "replace")
        o, n = self._loc(0x18)
        self.containers = [struct.unpack_from("<QQ", b, o + 16 * i) for i in range(n)]
        o, n = self._loc(0x30)
        self.stream = dict(zip(("raw_size", "packed_size", "count", "zero", "chunks"),
                               struct.unpack_from("<5I", b, o))) if n else {}
        o, n = self._loc(0x88)
        self.chunks = [struct.unpack_from("<5I", b, o + 20 * i) for i in range(n)]
        o, n = self._loc(0x38)
        self.order = struct.unpack_from(f"<{n}I", b, o) if n else ()
        ko, kn = self._loc(0x48)
        vo, vn = self._loc(0x50)
        assert kn == vn
        self.contents = {}
        for i in range(kn):
            key = ContentHash(*struct.unpack_from("<4I", b, ko + 16 * i))
            off, flags, cont = struct.unpack_from("<IHH", b, vo + 16 * i)
            self.contents[key] = (cont, off, flags)
        ko, kn = self._loc(0x60)
        vo, vn = self._loc(0x68)
        assert kn == vn
        self.keys = []
        self.values = []
        self.index = {}
        for i in range(kn):
            g = b[ko + 32 * i:ko + 32 * i + 16]
            th, part = struct.unpack_from("<II", b, ko + 32 * i + 16)
            k = ResourceKey(g, th, part)
            self.keys.append(k)
            self.values.append(struct.unpack_from("<II", b, vo + 8 * i))
            self.index[k] = i
        ko, kn = self._loc(0x78)
        vo, vn = self._loc(0x80)
        self.bundles = {}
        for i in range(kn):
            th = struct.unpack_from("<I", b, ko + 4 * i)[0]
            layout, first, count = struct.unpack_from("<3I", b, vo + 12 * i)
            self.bundles[th] = {"layout": layout, "first": first, "count": count}
        self._by_guid = None

    # ---- queries ----
    def summary(self):
        return {
            "file": str(self.path), "version": self.version, "containers": len(self.containers),
            "resources": len(self.keys), "contents": len(self.contents), "types": len(self.bundles),
            "stream": self.stream, "chunks": len(self.chunks),
        }

    def resources_of_type(self, type_hash):
        bd = self.bundles.get(type_hash)
        if not bd:
            return []
        return [self.keys[self.order[bd["first"] + i]] for i in range(bd["count"])]

    def by_guid(self, guid):
        """All resource keys (every type and part) that share a GUID."""
        if self._by_guid is None:
            m = {}
            for k in self.keys:
                m.setdefault(k[0], []).append(k)
            self._by_guid = m
        if isinstance(guid, str):
            guid = guid_bytes(guid)
        return self._by_guid.get(bytes(guid), [])

    def has(self, key):
        return key in self.index

    def size_of(self, key):
        return self.values[self.index[key]][1]

    # ---- resource bytes ----
    def _chunk(self, i):
        c = self._chunks_cache.get(i)
        if c is not None:
            self._chunks_cache.move_to_end(i)
            return c
        if self._res_file is None:
            self._res_file = open(self._resources, "rb")
        foff, stored, packed, doff, dsize = self.chunks[i]
        self._res_file.seek(foff)
        data = zstd_decompress(self._res_file.read(packed), dsize)
        if len(data) != dsize:
            raise ValueError(f"chunk {i}: {len(data)} bytes, expected {dsize}")
        self._chunks_cache[i] = data
        if len(self._chunks_cache) > self._chunk_cache_max:
            self._chunks_cache.popitem(last=False)
        return data

    def read_stream(self, offset, size):
        """Bytes [offset, offset+size) of the decompressed resource stream (may span chunks)."""
        out = bytearray()
        pos = offset
        end = offset + size
        # chunks are contiguous and sorted by decompressed offset
        lo, hi = 0, len(self.chunks) - 1
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if self.chunks[mid][3] <= pos:
                lo = mid
            else:
                hi = mid - 1
        i = lo
        while pos < end:
            if i >= len(self.chunks):
                raise ValueError("read past the resource stream")
            doff, dsize = self.chunks[i][3], self.chunks[i][4]
            if dsize == 0:
                i += 1
                continue
            data = self._chunk(i)
            a = pos - doff
            take = min(end - pos, dsize - a)
            out += data[a:a + take]
            pos += take
            i += 1
        return bytes(out)

    def read(self, key):
        if not isinstance(key, ResourceKey):
            key = ResourceKey(*key)
        off, size = self.values[self.index[key]]
        return self.read_stream(off, size)

    # ---- content blobs ----
    def dat_path(self, container):
        return Path(f"{self.stem}_{container:03d}.dat")

    def content_location(self, h):
        if not isinstance(h, ContentHash):
            h = ContentHash(h)
        loc = self.contents.get(h)
        if loc is None:
            return None
        cont, off, flags = loc
        return {"container": cont, "offset": off, "size": h.size, "flags": flags, "file": str(self.dat_path(cont))}

    def read_content(self, h):
        if not isinstance(h, ContentHash):
            h = ContentHash(h)
        loc = self.contents.get(h)
        if loc is None:
            raise KeyError(f"content {h} not in the directory")
        cont, off, _ = loc
        f = self._dat_files.get(cont)
        if f is None:
            f = self._dat_files[cont] = open(self.dat_path(cont), "rb")
        f.seek(off)
        data = f.read(h.size)
        if len(data) != h.size:
            raise ValueError(f"content {h}: short read ({len(data)} of {h.size})")
        return data

    def close(self):
        if self._res_file:
            self._res_file.close()
        for f in self._dat_files.values():
            f.close()
        self._dat_files.clear()
