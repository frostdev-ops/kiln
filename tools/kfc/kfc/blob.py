# SPDX-License-Identifier: MIT
"""Decode a reflected resource (a "blob": the struct, then its out-of-line data) into Python values.

Out-of-line kinds hold a signed 32-bit offset relative to the position of that offset field itself (verified on
ItemInfo, RenderModel and friends: every decoded array lands inside the resource and matches its counts):

    blob_array    {i32 rel, u32 count}        elements of the inner type, contiguous
    blob_string   {i32 rel, u32 length}       bytes, not NUL-terminated
    blob_optional {i32 rel}                   0 = empty, else one inner value
    blob_variant  {u32 type hash, i32 rel, u32 size}   the value of the derived type `hash`, 0 = empty

object_reference and guid are 16 bytes (keen::Guid memory order). Bitmask values are bit indices of the inner
flag enum.
"""
import struct

from . import refl as R
from .util import guid_text

_PRIM = {
    R.K_BOOL: "<?", R.K_U8: "<B", R.K_S8: "<b", R.K_U16: "<H", R.K_S16: "<h", R.K_U32: "<I", R.K_S32: "<i",
    R.K_U64: "<Q", R.K_S64: "<q", R.K_F32: "<f", R.K_F64: "<d",
}
_INT_BY_SIZE = {1: "<B", 2: "<H", 4: "<I", 8: "<Q"}
_SINT_BY_SIZE = {1: "<b", 2: "<h", 4: "<i", 8: "<q"}


class DecodeError(Exception):
    pass


class Decoder:
    """Stateless apart from the type db and limits. `decode(type, buf, off)` returns plain Python values:
    dict for structs (field order kept), list for arrays, str for strings/guids/enums, int/float/bool scalars."""

    def __init__(self, types, enum_names=True, max_array=1 << 20, keep_raw_offsets=False):
        self.t = types
        self.enum_names = enum_names
        self.max_array = max_array
        self.keep_raw_offsets = keep_raw_offsets

    def decode(self, type_name, buf, off=0, depth=0):
        t = self.t.get(type_name) if isinstance(type_name, (str, int)) else type_name
        if t is None:
            raise DecodeError(f"unknown type {type_name}")
        return self._value(t, buf, off, depth)

    # ---- internals ----
    def _value(self, t, b, o, depth):
        if depth > 64:
            raise DecodeError("nesting too deep")
        k = t["kind"]
        if k in _PRIM:
            return struct.unpack_from(_PRIM[k], b, o)[0]
        if k == R.K_TYPEDEF:
            inner = self.t.get(t["inner"])
            if inner is None:
                return b[o:o + t["size"]].hex()
            return self._value(inner, b, o, depth)
        if k == R.K_ENUM:
            under = self.t.resolve(self.t.get(t["inner"])) if t.get("inner") else None
            signed = under is not None and under["kind"] in (R.K_S8, R.K_S16, R.K_S32, R.K_S64)
            fmt = (_SINT_BY_SIZE if signed else _INT_BY_SIZE).get(t["size"])
            v = struct.unpack_from(fmt, b, o)[0]
            if self.enum_names:
                for e in t.get("values", []):
                    if e["value"] == (v & ((1 << (8 * t["size"])) - 1)):
                        return e["name"]
            return v
        if R.K_BM8 <= k <= R.K_BM64:
            v = struct.unpack_from(_INT_BY_SIZE[t["size"]], b, o)[0]
            en = self.t.get(t["inner"]) if t.get("inner") else None
            if not self.enum_names or en is None:
                return v
            names = {e["value"]: e["name"] for e in en.get("values", [])}
            return [names.get(i, i) for i in range(8 * t["size"]) if (v >> i) & 1]
        if k == R.K_STRUCT:
            out = {}
            for f in self.t.all_fields(t):
                ft = self.t.get(f["type"])
                if ft is None:
                    out[f["name"]] = None
                    continue
                out[f["name"]] = self._value(ft, b, o + f["offset"], depth + 1)
            return out
        if k == R.K_STATIC_ARRAY:
            el = self.t.get(t["inner"])
            n = t["count"]
            es = el["size"] if el and el["size"] else (t["size"] // n if n else 0)
            return [self._value(el, b, o + i * es, depth + 1) for i in range(n)]
        if k in (R.K_OBJECT_REF, R.K_GUID):
            return guid_text(b[o:o + 16])
        if k == R.K_BLOB_ARRAY:
            rel, n = struct.unpack_from("<iI", b, o)
            if n == 0:
                return []
            if n > self.max_array:
                raise DecodeError(f"{t['name']}: {n} elements")
            el = self.t.get(t["inner"])
            es = el["size"]
            base = o + rel
            if base < 0 or base + n * es > len(b):
                raise DecodeError(f"{t['name']} at {o:#x}: [{base:#x}, +{n}x{es}) outside the blob ({len(b)})")
            if el["kind"] in (R.K_U8,) and n > 64:
                return {"bytes": b[base:base + n].hex()}
            return [self._value(el, b, base + i * es, depth + 1) for i in range(n)]
        if k == R.K_BLOB_STRING:
            rel, n = struct.unpack_from("<iI", b, o)
            if n == 0:
                return ""
            return b[o + rel:o + rel + n].decode("utf-8", "replace")
        if k == R.K_BLOB_OPTIONAL:
            rel = struct.unpack_from("<i", b, o)[0]
            if rel == 0:
                return None
            el = self.t.get(t["inner"])
            return self._value(el, b, o + rel, depth + 1)
        if k == R.K_BLOB_VARIANT:
            h, rel, size = struct.unpack_from("<IiI", b, o)
            if h == 0:
                return None
            vt = self.t.get(h)
            if vt is None:
                return {"$type": f"0x{h:08x}", "$size": size}
            v = self._value(vt, b, o + 4 + rel, depth + 1)
            return {"$type": vt["name"], "value": v}
        # ds_* kinds and unknowns: raw bytes
        return {"$raw": b[o:o + t["size"]].hex(), "$kind": R.KIND_NAMES.get(k, k)}


def blob_range(b, o, rel_at_field=True):
    """(start, count) of a blob_array header at `o`."""
    rel, n = struct.unpack_from("<iI", b, o)
    return (o + rel if n else 0), n
