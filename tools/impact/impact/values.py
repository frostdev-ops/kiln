# SPDX-License-Identifier: MIT
"""Typed initial values: variable bytes <-> .kimp value text, driven by the reflection records in types.json.

Syntax (also accepted by `set` edits and cook recipes):
    2.5  -3  0x1f            numbers (float32 values print in the shortest form that keeps the bits)
    true false               bool
    250ms 2.5s 100ns         keen::Time (stored as i64 nanoseconds)
    Medium                   an enum value name
    [Magic, AoE]             a bitmask (flag names or bit numbers) or a static array
    (1, 0, 0)                a struct, fields in order
    {x: 1, z: 2}             a struct by field names (missing fields = 0)
    3f1e0114-...-...         a GUID (ObjectReference: templates, buffs, attributes, ...)
    hex:0000803f             raw bytes, any type (the fallback when a value has no exact typed form)
"""
import math
import re
import struct
import uuid

R = None  # kfc.refl, imported lazily (keeps the codec usable without tools/kfc)


def _refl():
    global R
    if R is None:
        from kfc import refl
        R = refl
    return R


class ValueError_(Exception):
    pass


_PRIM_FMT = {1: "<?", 2: "<B", 3: "<b", 4: "<H", 5: "<h", 6: "<I", 7: "<i", 8: "<Q", 9: "<q", 10: "<f", 11: "<d"}
_UINT = {1: "<B", 2: "<H", 4: "<I", 8: "<Q"}
_SINT = {1: "<b", 2: "<h", 4: "<i", 8: "<q"}
TIME_TYPE = "keen::Time"


def f32_text(x):
    """Shortest decimal that packs back to the same float32 bits."""
    bits = struct.pack("<f", x)
    if math.isnan(x) or math.isinf(x):
        return None
    for p in range(1, 10):
        s = f"{x:.{p}g}"
        if struct.pack("<f", float(s)) == bits:
            if "e" in s and 1e-5 <= abs(x) < 1e9:
                s = f"{float(s):.{max(0, p - 1 - int(math.floor(math.log10(abs(x)))))}f}"
            if "e" not in s and "." not in s:
                s += ".0"
            if s == "-0.0" or s == "-0":
                s = "-0.0"
            if struct.pack("<f", float(s)) == bits:
                return s
    return repr(x)


def time_text(ns):
    if ns % 1_000_000_000 == 0 and ns:
        return f"{ns // 1_000_000_000}s"
    if ns % 1_000_000 == 0:
        return f"{ns // 1_000_000}ms"
    return f"{ns}ns"


# ---------------------------------------------------------------- bytes -> text
def format_value(db, tname, b, off=0):
    """Text for the value of reflected type `tname` at b[off:]. Raises ValueError_ when the type is not known."""
    rf = _refl()
    t = db.get(tname)
    if t is None:
        raise ValueError_(f"type {tname} not in the catalog")
    if t["name"] == TIME_TYPE:
        return time_text(struct.unpack_from("<q", b, off)[0])
    k = t["kind"]
    if k == rf.K_BOOL:
        v = b[off]
        if v > 1:
            raise ValueError_("bool byte is not 0/1")
        return "true" if v else "false"
    if k == rf.K_F32:
        s = f32_text(struct.unpack_from("<f", b, off)[0])
        if s is None:
            raise ValueError_("nan/inf")
        return s
    if k == rf.K_F64:
        x = struct.unpack_from("<d", b, off)[0]
        if math.isnan(x) or math.isinf(x):
            raise ValueError_("nan/inf")
        return repr(x)
    if k in _PRIM_FMT:
        v = struct.unpack_from(_PRIM_FMT[k], b, off)[0]
        return hex(v) if k in (6, 8) and v > 0xFFFF else str(v)
    if k == rf.K_TYPEDEF:
        return format_value(db, t["inner"], b, off)
    if k == rf.K_ENUM:
        v = struct.unpack_from(_UINT[t["size"]], b, off)[0]
        for e in t.get("values", []):
            if e["value"] == v:
                return e["name"]
        return str(v)
    if rf.K_BM8 <= k <= rf.K_BM64:
        v = struct.unpack_from(_UINT[t["size"]], b, off)[0]
        en = db.get(t["inner"]) if t.get("inner") else None
        names = {e["value"]: e["name"] for e in (en or {}).get("values", [])}
        return "[" + ", ".join(names.get(i, str(i)) for i in range(8 * t["size"]) if (v >> i) & 1) + "]"
    if k in (rf.K_OBJECT_REF, rf.K_GUID):
        return str(uuid.UUID(bytes_le=bytes(b[off:off + 16])))
    if k == rf.K_STATIC_ARRAY:
        el = db.get(t["inner"])
        n = t["count"]
        es = el["size"] if el and el["size"] else t["size"] // max(1, n)
        return "[" + ", ".join(format_value(db, t["inner"], b, off + i * es) for i in range(n)) + "]"
    if k == rf.K_STRUCT:
        fields = db.all_fields(t)
        if len(fields) == 1 and fields[0]["offset"] == 0:
            return format_value(db, fields[0]["type"], b, off)
        parts = [(f["name"], format_value(db, f["type"], b, off + f["offset"])) for f in fields]
        simple = len(parts) <= 4 and all(db.resolve(db.get(f["type"]))["kind"] in _PRIM_FMT for f in fields)
        if simple:
            return "(" + ", ".join(v for _, v in parts) + ")"
        nz = [(n, v) for (n, v), f in zip(parts, fields) if any(b[off + f["offset"]:off + f["offset"] + (db.get(f["type"]) or {}).get("size", 0)])]
        return "{" + ", ".join(f"{n}: {v}" for n, v in nz) + "}"
    raise ValueError_(f"type {tname}: kind {k} has no text form")


# ---------------------------------------------------------------- text -> AST
_TOK = re.compile(r"""\s*(?:
    (?P<hex>hex:[0-9A-Fa-f]*)|
    (?P<guid>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})|
    (?P<num>[-+]?(?:0[xX][0-9A-Fa-f]+|(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)(?:ns|ms|s)?)|
    (?P<str>"(?:[^"\\]|\\.)*")|
    (?P<id>[A-Za-z_][A-Za-z0-9_]*)|
    (?P<p>[\[\](){},:]))""", re.X)


def tokenize(s):
    out, pos = [], 0
    s = s.strip()
    while pos < len(s):
        m = _TOK.match(s, pos)
        if not m or m.end() == pos:
            raise ValueError_(f"bad value text at {s[pos:pos + 20]!r}")
        kind = m.lastgroup
        out.append((kind, m.group(kind)))
        pos = m.end()
        while pos < len(s) and s[pos].isspace():
            pos += 1
    return out


def parse_ast(s):
    toks = tokenize(s)
    i = 0

    def val():
        nonlocal i
        if i >= len(toks):
            raise ValueError_("value expected")
        k, t = toks[i]
        i += 1
        if k == "p" and t in "[(":
            close = "]" if t == "[" else ")"
            items = []
            while not (i < len(toks) and toks[i] == ("p", close)):
                items.append(val())
                if i < len(toks) and toks[i] == ("p", ","):
                    i += 1
            i += 1
            return ("list" if t == "[" else "tuple", items)
        if k == "p" and t == "{":
            items = {}
            while not (i < len(toks) and toks[i] == ("p", "}")):
                kk, name = toks[i]
                if kk not in ("id", "str"):
                    raise ValueError_(f"field name expected, got {name!r}")
                i += 1
                if toks[i] != ("p", ":"):
                    raise ValueError_("':' expected")
                i += 1
                items[name.strip('"')] = val()
                if i < len(toks) and toks[i] == ("p", ","):
                    i += 1
            i += 1
            return ("dict", items)
        if k == "p":
            raise ValueError_(f"unexpected {t!r}")
        return (k, t)

    v = val()
    if i != len(toks):
        raise ValueError_(f"trailing text in value {s!r}")
    return v


def _num(t, want_float=False):
    m = re.match(r"^(.*?)(ns|ms|s)?$", t)
    body, unit = m.group(1), m.group(2)
    if unit and not body.lower().startswith(("0x", "-0x", "+0x")):
        x = float(body)
        mul = {"ns": 1, "ms": 1_000_000, "s": 1_000_000_000}[unit]
        return int(round(x * mul)), True
    if body.lower().startswith(("0x", "-0x", "+0x")):
        return int(body, 16), False
    if any(c in body for c in ".eE") or want_float:
        return float(body), False
    return int(body), False


# ---------------------------------------------------------------- AST -> bytes
def encode_value(db, tname, ast, size=None):
    rf = _refl()
    if ast[0] == "hex":
        raw = bytes.fromhex(ast[1][4:])
        return raw
    t = db.get(tname)
    if t is None:
        raise ValueError_(f"type {tname} not in the catalog: use hex:...")
    out = bytearray(t["size"])
    _enc(db, t, ast, out, 0)
    return bytes(out)


def _enc(db, t, ast, out, o):
    rf = _refl()
    k = t["kind"]
    kind, v = ast
    if t["name"] == TIME_TYPE:
        if kind != "num":
            raise ValueError_("Time: a number with ns/ms/s expected")
        n, unit = _num(v)
        if not unit:
            n = int(n)  # bare number = nanoseconds
        struct.pack_into("<q", out, o, int(n))
        return
    if k == rf.K_TYPEDEF:
        return _enc(db, db.get(t["inner"]), ast, out, o)
    if k == rf.K_BOOL:
        if kind == "id" and v in ("true", "false"):
            out[o] = 1 if v == "true" else 0
            return
        if kind == "num":
            out[o] = int(_num(v)[0]) & 0xFF
            return
        raise ValueError_(f"bool: {v!r}")
    if k in (rf.K_F32, rf.K_F64):
        if kind != "num":
            raise ValueError_(f"float: {v!r}")
        x = float(_num(v, True)[0])
        struct.pack_into("<f" if k == rf.K_F32 else "<d", out, o, x)
        return
    if k in _PRIM_FMT:
        if kind == "id" and v in ("true", "false"):
            x = 1 if v == "true" else 0
        elif kind == "num":
            x = int(_num(v)[0])
        else:
            raise ValueError_(f"integer: {v!r}")
        sz = t["size"]
        x &= (1 << (8 * sz)) - 1
        struct.pack_into(_UINT[sz], out, o, x)
        return
    if k == rf.K_ENUM:
        if kind == "id":
            for e in t.get("values", []):
                if e["name"] == v:
                    struct.pack_into(_UINT[t["size"]], out, o, e["value"])
                    return
            raise ValueError_(f"{t['name']}: no value {v!r}")
        if kind == "num":
            struct.pack_into(_UINT[t["size"]], out, o, int(_num(v)[0]) & ((1 << (8 * t["size"])) - 1))
            return
        raise ValueError_(f"enum {t['name']}: {v!r}")
    if rf.K_BM8 <= k <= rf.K_BM64:
        en = db.get(t["inner"]) if t.get("inner") else None
        names = {e["name"]: e["value"] for e in (en or {}).get("values", [])}
        if kind == "num":
            bits = int(_num(v)[0])
        elif kind == "list":
            bits = 0
            for kk, x in v:
                if kk == "id" and x in names:
                    bits |= 1 << names[x]
                elif kk == "num":
                    bits |= 1 << int(x, 0)
                else:
                    raise ValueError_(f"{t['name']}: no flag {x!r}")
        else:
            raise ValueError_(f"bitmask {t['name']}: [flags] expected")
        struct.pack_into(_UINT[t["size"]], out, o, bits)
        return
    if k in (rf.K_OBJECT_REF, rf.K_GUID):
        if kind != "guid":
            raise ValueError_(f"{t['name']}: a GUID expected")
        out[o:o + 16] = uuid.UUID(v).bytes_le
        return
    if k == rf.K_STATIC_ARRAY:
        el = db.get(t["inner"])
        n = t["count"]
        es = el["size"] if el["size"] else t["size"] // max(1, n)
        if kind not in ("list", "tuple") or len(v) > n:
            raise ValueError_(f"{t['name']}: up to {n} values in [...]")
        for i, x in enumerate(v):
            _enc(db, el, x, out, o + i * es)
        return
    if k == rf.K_STRUCT:
        fields = db.all_fields(t)
        if len(fields) == 1 and fields[0]["offset"] == 0 and kind not in ("dict",):
            if not (kind == "tuple" and len(v) == 1):
                return _enc(db, db.get(fields[0]["type"]), ast, out, o)
            ast = v[0]
            return _enc(db, db.get(fields[0]["type"]), ast, out, o)
        if kind == "tuple":
            if len(v) > len(fields):
                raise ValueError_(f"{t['name']}: {len(fields)} fields, {len(v)} values")
            for f, x in zip(fields, v):
                _enc(db, db.get(f["type"]), x, out, o + f["offset"])
            return
        if kind == "dict":
            by = {f["name"]: f for f in fields}
            for name, x in v.items():
                if name not in by:
                    raise ValueError_(f"{t['name']}: no field {name!r} (fields: {', '.join(by)})")
                f = by[name]
                _enc(db, db.get(f["type"]), x, out, o + f["offset"])
            return
        raise ValueError_(f"{t['name']}: (a, b, ...) or {{field: v}} expected")
    raise ValueError_(f"{t['name']}: kind {k} not encodable")


def exact_text(db, tname, raw):
    """Text for `raw` (the variable's bytes) that encodes back to exactly `raw`, or None."""
    if db is None or tname is None:
        return None
    try:
        s = format_value(db, tname, raw, 0)
        if encode_value(db, tname, parse_ast(s)) == bytes(raw):
            return s
    except (ValueError_, struct.error, IndexError, KeyError, TypeError, ValueError):
        return None
    return None
