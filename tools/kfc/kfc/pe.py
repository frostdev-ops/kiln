# SPDX-License-Identifier: MIT
"""Minimal PE32+ image loader: maps sections at their RVAs so absolute pointers (preferred base) can be followed."""
import hashlib
import struct
from pathlib import Path


class PeImage:
    def __init__(self, path):
        raw = Path(path).read_bytes()
        self.sha256 = hashlib.sha256(raw).hexdigest()
        if raw[:2] != b"MZ":
            raise ValueError(f"{path}: not a PE file")
        pe = struct.unpack_from("<I", raw, 0x3C)[0]
        if raw[pe:pe + 4] != b"PE\0\0":
            raise ValueError(f"{path}: no PE signature")
        nsec = struct.unpack_from("<H", raw, pe + 6)[0]
        optsz = struct.unpack_from("<H", raw, pe + 20)[0]
        magic = struct.unpack_from("<H", raw, pe + 24)[0]
        if magic != 0x20B:
            raise ValueError(f"{path}: not PE32+")
        self.base = struct.unpack_from("<Q", raw, pe + 24 + 24)[0]
        self.sections = {}
        off = pe + 24 + optsz
        secs = []
        size = 0
        for _ in range(nsec):
            name = raw[off:off + 8].rstrip(b"\0").decode("latin1")
            vsz, va, rsz, rawoff = struct.unpack_from("<IIII", raw, off + 8)
            secs.append((name, va, vsz, rawoff, rsz))
            size = max(size, va + max(vsz, rsz))
            off += 40
        img = bytearray(size)
        for name, va, vsz, rawoff, rsz in secs:
            n = min(vsz, rsz)
            img[va:va + n] = raw[rawoff:rawoff + n]
            self.sections[name] = (va, vsz)
        self.img = bytes(img)

    def rva(self, va):
        """Absolute VA (preferred base) -> RVA, or None when outside the image."""
        r = va - self.base
        return r if 0 <= r < len(self.img) else None

    def q(self, rva):
        return struct.unpack_from("<Q", self.img, rva)[0]

    def d(self, rva):
        return struct.unpack_from("<I", self.img, rva)[0]

    def w(self, rva):
        return struct.unpack_from("<H", self.img, rva)[0]

    def text_at(self, va, length):
        r = self.rva(va)
        if r is None or length <= 0 or length > 4096:
            return None
        return self.img[r:r + length].decode("utf-8", "replace")
