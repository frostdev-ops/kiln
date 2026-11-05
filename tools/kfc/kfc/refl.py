# SPDX-License-Identifier: MIT
"""The game's own reflection, read offline from the executable (client or dedicated server).

Every keen:: type has a static record in .data/.rdata (the record `content/TypeByHash` returns at run time; layout in
docs/re/settings.md and docs/re/content.md):

    +0x00/+0x08 short name ptr/len, +0x10/+0x18 impact name, +0x20/+0x28 qualified name ptr/len,
    +0x38 inner type record (typedef target, enum underlying type, array/blob element, struct base),
    +0x40 u32 size, +0x44 u16 alignment, +0x48 u32 field/enum-value/static-array count, +0x4C u32 kind (low byte;
    upper bits flags), +0x50 u32 hash = FNV-1a(qualified name), +0x54 u32 layout hash (the same value the .kfc
    stores per resource type bundle), +0x58 fields (stride 0x30: name ptr, len, type record, u64 offset),
    +0x60 enum values (stride 0x28: name ptr, len, u64 value).

The pointers are absolute VAs at the preferred image base (the records are static data), so no relocation is
needed. A record is accepted only when the hash at +0x50 equals FNV-1a of the name it points at.
"""
import json
import struct
from pathlib import Path

from .pe import PeImage
from .util import fnv1a32, out_dir

# kinds (low byte of +0x4C)
K_NONE, K_BOOL, K_U8, K_S8, K_U16, K_S16, K_U32, K_S32, K_U64, K_S64, K_F32, K_F64 = range(12)
K_ENUM, K_BM8, K_BM16, K_BM32, K_BM64, K_TYPEDEF, K_STRUCT, K_STATIC_ARRAY = range(0x0C, 0x14)
K_DS_ARRAY, K_DS_STRING, K_DS_OPTIONAL, K_DS_VARIANT = range(0x14, 0x18)
K_BLOB_ARRAY, K_BLOB_STRING, K_BLOB_OPTIONAL, K_BLOB_VARIANT, K_OBJECT_REF, K_GUID = range(0x18, 0x1E)

KIND_NAMES = {
    K_NONE: "none", K_BOOL: "bool", K_U8: "u8", K_S8: "s8", K_U16: "u16", K_S16: "s16", K_U32: "u32", K_S32: "s32",
    K_U64: "u64", K_S64: "s64", K_F32: "f32", K_F64: "f64", K_ENUM: "enum", K_BM8: "bitmask8", K_BM16: "bitmask16",
    K_BM32: "bitmask32", K_BM64: "bitmask64", K_TYPEDEF: "typedef", K_STRUCT: "struct",
    K_STATIC_ARRAY: "static_array", K_DS_ARRAY: "ds_array", K_DS_STRING: "ds_string", K_DS_OPTIONAL: "ds_optional",
    K_DS_VARIANT: "ds_variant", K_BLOB_ARRAY: "blob_array", K_BLOB_STRING: "blob_string",
    K_BLOB_OPTIONAL: "blob_optional", K_BLOB_VARIANT: "blob_variant", K_OBJECT_REF: "object_reference",
    K_GUID: "guid",
}

REC_QNAME, REC_INNER, REC_SIZE, REC_ALIGN, REC_COUNT, REC_KIND = 0x20, 0x38, 0x40, 0x44, 0x48, 0x4C
REC_HASH, REC_LAYOUT, REC_FIELDS, REC_ENUMS, REC_LEN = 0x50, 0x54, 0x58, 0x60, 0x68
FIELD_STRIDE, ENUM_STRIDE = 0x30, 0x28


def scan(pe):
    """Return {record_rva: qualified_name} for every validated record in .data/.rdata."""
    found = {}
    img, base = pe.img, pe.base
    end = base + len(img)
    for sec in (".data", ".rdata"):
        if sec not in pe.sections:
            continue
        va, vsz = pe.sections[sec]
        for o in range(va, va + vsz - REC_LEN, 8):
            p, n = struct.unpack_from("<QQ", img, o + REC_QNAME)
            if not (base <= p < end) or not (1 <= n <= 512):
                continue
            s = img[p - base:p - base + n]
            if struct.unpack_from("<I", img, o + REC_HASH)[0] == fnv1a32(s):
                found[o] = s.decode("utf-8", "replace")
    return found


def extract(exe_path):
    """Read every reflection record of an executable into a JSON-able dict."""
    pe = PeImage(exe_path)
    recs = scan(pe)

    def ref(va):
        if not va:
            return None
        r = pe.rva(va)
        return recs.get(r) if r is not None else None

    types = []
    for rva, name in sorted(recs.items(), key=lambda kv: kv[1]):
        kind_raw = pe.d(rva + REC_KIND)
        kind = kind_raw & 0xFF
        count = pe.d(rva + REC_COUNT)
        t = {
            "name": name,
            "hash": pe.d(rva + REC_HASH),
            "layout": pe.d(rva + REC_LAYOUT),
            "size": pe.d(rva + REC_SIZE),
            "align": pe.w(rva + REC_ALIGN),
            "kind": kind,
            "flags": kind_raw >> 8,
            "count": count,
            "inner": ref(pe.q(rva + REC_INNER)),
            "rva": rva,
        }
        fp = pe.q(rva + REC_FIELDS)
        if kind == K_STRUCT and fp and count < 4096:
            fr = pe.rva(fp)
            fields = []
            for i in range(count):
                e = fr + i * FIELD_STRIDE
                fields.append({
                    "name": pe.text_at(pe.q(e), pe.q(e + 8)) or "",
                    "type": ref(pe.q(e + 0x10)),
                    "offset": pe.q(e + 0x18),
                })
            t["fields"] = fields
        ep = pe.q(rva + REC_ENUMS)
        if kind in (K_ENUM,) and ep and count < 65536:
            er = pe.rva(ep)
            vals = []
            for i in range(count):
                e = er + i * ENUM_STRIDE
                vals.append({"name": pe.text_at(pe.q(e), pe.q(e + 8)) or "", "value": pe.q(e + 0x10)})
            t["values"] = vals
        types.append(t)
    return {"exe_sha256": pe.sha256, "image_base": pe.base, "count": len(types), "types": types}


class TypeDb:
    """Type records by qualified name and by hash."""

    def __init__(self, dump):
        self.dump = dump
        self.by_name = {t["name"]: t for t in dump["types"]}
        self.by_hash = {t["hash"]: t for t in dump["types"]}

    @classmethod
    def load(cls, path):
        return cls(json.loads(Path(path).read_text()))

    @classmethod
    def from_exe(cls, exe_path, cache=True):
        """Extract (or load the cached extraction from .out/models/types/<sha16>.json)."""
        import hashlib
        h = hashlib.sha256(Path(exe_path).read_bytes()).hexdigest()
        cp = out_dir("types") / f"{h[:16]}.json"
        if cache and cp.exists():
            return cls.load(cp)
        dump = extract(exe_path)
        if cache:
            cp.write_text(json.dumps(dump))
        return cls(dump)

    def get(self, key):
        """By qualified name, hash (int) or '0x...' string."""
        if isinstance(key, int):
            return self.by_hash.get(key)
        if isinstance(key, str) and key.startswith("0x"):
            return self.by_hash.get(int(key, 16))
        return self.by_name.get(key)

    def name(self, h):
        t = self.by_hash.get(h)
        return t["name"] if t else f"0x{h:08x}"

    def resolve(self, t):
        """Follow typedefs to the final record."""
        for _ in range(16):
            if t is None or t["kind"] != K_TYPEDEF:
                return t
            t = self.by_name.get(t["inner"])
        return t

    def all_fields(self, t):
        """Fields of a struct including its base chain (base first)."""
        out = []
        chain = []
        cur = t
        while cur is not None and cur["kind"] == K_STRUCT:
            chain.append(cur)
            cur = self.by_name.get(cur["inner"]) if cur.get("inner") else None
        for c in reversed(chain):
            out.extend(c.get("fields", []))
        return out

    def closure(self, names):
        """Every type reachable from `names` (fields, inner types, enum underlying types): a small TypeDb dump for
        fixtures and offline tools that need only a few layouts."""
        seen, todo = {}, list(names)
        while todo:
            n = todo.pop()
            if not n or n in seen:
                continue
            t = self.by_name.get(n)
            if t is None:
                continue
            seen[n] = t
            todo.append(t.get("inner"))
            todo.extend(f["type"] for f in t.get("fields", []))
        out = dict(self.dump)
        out["types"] = [seen[n] for n in sorted(seen)]
        out["count"] = len(seen)
        out["closure_of"] = list(names)
        return out

    def describe(self, name, depth=0, max_depth=1):
        """Human-readable layout (for `kfc.py types --show`)."""
        t = self.get(name)
        if t is None:
            return f"{name}: unknown"
        lines = [f"{t['name']}  size {t['size']} align {t['align']} kind {KIND_NAMES.get(t['kind'], t['kind'])}"
                 f" hash 0x{t['hash']:08x} layout 0x{t['layout']:08x}" + (f" inner {t['inner']}" if t['inner'] else "")]
        for f in self.all_fields(t) if t["kind"] == K_STRUCT else []:
            lines.append(f"  +0x{f['offset']:04x} {f['name']}: {f['type']}")
        for v in t.get("values", []):
            lines.append(f"  {v['name']} = {v['value']}")
        return "\n".join(lines)
