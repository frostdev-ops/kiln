"""Minimal PE image loader + AOB scanner (stdlib only) for signature work."""
import re
import struct
from pathlib import Path


class Image:
    def __init__(self, path):
        b = Path(path).read_bytes()
        pe = struct.unpack_from("<I", b, 0x3C)[0]
        nsec = struct.unpack_from("<H", b, pe + 6)[0]
        optsz = struct.unpack_from("<H", b, pe + 20)[0]
        self.base = struct.unpack_from("<Q", b, pe + 24 + 24)[0]
        self.sections = {}
        off = pe + 24 + optsz
        size = 0
        secs = []
        for _ in range(nsec):
            name = b[off:off + 8].rstrip(b"\0").decode()
            vsz, va, rsz, raw = struct.unpack_from("<IIII", b, off + 8)
            secs.append((name, va, vsz, raw, rsz))
            size = max(size, va + max(vsz, rsz))
            off += 40
        img = bytearray(size)
        for name, va, vsz, raw, rsz in secs:
            n = min(vsz, rsz)
            img[va:va + n] = b[raw:raw + n]
            self.sections[name] = (va, vsz)
        self.img = bytes(img)
        va, vsz = self.sections[".text"]
        self.text_va, self.text = va, self.img[va:va + vsz]

    @staticmethod
    def compile(pattern):
        parts = pattern.split()
        rx = b"".join(b"." if p in ("??", "?") else re.escape(bytes([int(p, 16)])) for p in parts)
        return re.compile(rx, re.S)

    def find(self, pattern, limit=5):
        """RVAs of matches in .text."""
        rx = self.compile(pattern)
        out = []
        for m in rx.finditer(self.text):
            out.append(self.text_va + m.start())
            if len(out) >= limit:
                break
        return out
