# SPDX-License-Identifier: MIT
"""Encode Python values (as produced by blob.Decoder) back into a reflected resource blob.

Layout rule (Kiln's reading; checked by re-encoding the game's own resources byte for byte, see tests and
docs/re/models.md): the root struct comes first; out-of-line data is appended behind it in the order the fields are
met (depth first), each block aligned to its element type's alignment. A blob_array reserves its whole element block
first, then the elements' own out-of-line data follows. Offsets are self-relative (target - position of the offset
field). The total is not padded: a resource ends with its last byte of data.
"""
import struct

from . import refl as R
from .util import guid_bytes

_PRIM = {
    R.K_BOOL: "<?", R.K_U8: "<B", R.K_S8: "<b", R.K_U16: "<H", R.K_S16: "<h", R.K_U32: "<I", R.K_S32: "<i",
    R.K_U64: "<Q", R.K_S64: "<q", R.K_F32: "<f", R.K_F64: "<d",
}
_INT_BY_SIZE = {1: "<B", 2: "<H", 4: "<I", 8: "<Q"}


class EncodeError(Exception):
    pass


def _align(n, a):
    a = max(1, a)
    return (n + a - 1) // a * a


class Encoder:
    def __init__(self, types):
        self.t = types

    def encode(self, type_name, value):
        t = self.t.get(type_name) if isinstance(type_name, (str, int)) else type_name
        if t is None:
            raise EncodeError(f"unknown type {type_name}")
        self.buf = bytearray(t["size"])
        self._write(t, value, 0)
        return bytes(self.buf)

    # out-of-line block: aligned append, returns its offset
    def _reserve(self, size, align):
        o = _align(len(self.buf), align)
        self.buf.extend(b"\0" * (o - len(self.buf) + size))
        return o

    def _write(self, t, v, o):
        k = t["kind"]
        b = self.buf
        if k in _PRIM:
            struct.pack_into(_PRIM[k], b, o, v)
            return
        if k == R.K_TYPEDEF:
            self._write(self.t.get(t["inner"]), v, o)
            return
        if k == R.K_ENUM:
            if isinstance(v, str):
                m = {e["name"]: e["value"] for e in t.get("values", [])}
                if v not in m:
                    raise EncodeError(f"{t['name']}: unknown value {v!r}")
                v = m[v]
            v &= (1 << (8 * t["size"])) - 1
            struct.pack_into(_INT_BY_SIZE[t["size"]], b, o, v)
            return
        if R.K_BM8 <= k <= R.K_BM64:
            if isinstance(v, list):
                en = self.t.get(t["inner"]) if t.get("inner") else None
                names = {e["name"]: e["value"] for e in (en or {}).get("values", [])}
                bits = 0
                for x in v:
                    bits |= 1 << (names[x] if isinstance(x, str) else x)
                v = bits
            struct.pack_into(_INT_BY_SIZE[t["size"]], b, o, v)
            return
        if k == R.K_STRUCT:
            if not isinstance(v, dict):
                raise EncodeError(f"{t['name']}: expected a dict")
            for f in self.t.all_fields(t):
                if f["name"] not in v:
                    continue  # missing fields stay zero
                self._write(self.t.get(f["type"]), v[f["name"]], o + f["offset"])
            return
        if k == R.K_STATIC_ARRAY:
            el = self.t.get(t["inner"])
            n = t["count"]
            es = el["size"] if el["size"] else t["size"] // max(1, n)
            for i, x in enumerate(v[:n]):
                self._write(el, x, o + i * es)
            return
        if k in (R.K_OBJECT_REF, R.K_GUID):
            g = guid_bytes(v) if isinstance(v, str) else bytes(v)
            b[o:o + 16] = g
            return
        if k == R.K_BLOB_ARRAY:
            el = self.t.get(t["inner"])
            if isinstance(v, dict) and "bytes" in v:
                data = bytes.fromhex(v["bytes"])
                n = len(data)
                if n == 0:
                    struct.pack_into("<iI", b, o, 0, 0)
                    return
                at = self._reserve(n, el["align"])
                self.buf[at:at + n] = data
                struct.pack_into("<iI", self.buf, o, at - o, n)
                return
            n = len(v)
            if n == 0:
                struct.pack_into("<iI", b, o, 0, 0)
                return
            es = el["size"]
            at = self._reserve(n * es, el["align"])
            struct.pack_into("<iI", self.buf, o, at - o, n)
            for i, x in enumerate(v):
                self._write(el, x, at + i * es)
            return
        if k == R.K_BLOB_STRING:
            data = v.encode() if isinstance(v, str) else bytes(v)
            if not data:
                struct.pack_into("<iI", b, o, 0, 0)
                return
            at = self._reserve(len(data), 1)
            self.buf[at:at + len(data)] = data
            struct.pack_into("<iI", self.buf, o, at - o, len(data))
            return
        if k == R.K_BLOB_OPTIONAL:
            if v is None:
                struct.pack_into("<i", b, o, 0)
                return
            el = self.t.get(t["inner"])
            at = self._reserve(el["size"], el["align"])
            struct.pack_into("<i", self.buf, o, at - o)
            self._write(el, v, at)
            return
        if k == R.K_BLOB_VARIANT:
            if v is None:
                struct.pack_into("<IiI", b, o, 0, 0, 0)
                return
            vt = self.t.get(v["$type"])
            if vt is None:
                raise EncodeError(f"variant type {v['$type']} unknown")
            at = self._reserve(vt["size"], vt["align"])
            # the size field is the variant struct's own size, not counting its out-of-line data
            struct.pack_into("<IiI", self.buf, o, vt["hash"], at - (o + 4), vt["size"])
            self._write(vt, v["value"], at)
            return
        if isinstance(v, dict) and "$raw" in v:
            raw = bytes.fromhex(v["$raw"])
            b[o:o + len(raw)] = raw
            return
        raise EncodeError(f"{t['name']}: kind {R.KIND_NAMES.get(k, k)} not encodable")
