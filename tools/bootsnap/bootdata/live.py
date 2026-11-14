# SPDX-License-Identifier: MIT
"""A resource blob as the JSON kiln.content's live game-data reader gives for it after world load.

The live reader is native/services/content/gamedata_walk.cpp (`node_json`, `leaf_of`, `leaf_json`, `deref`, `elements`,
the config vocabulary) over the type graph of gamedata_types.cpp, and a whole record is `node_json(root, 12)`
(gamedata_api.cpp, `t_get_json` with an empty path). This module follows it rule for rule over a blob in a byte buffer
instead of live memory, so that the boot game-data snapshot (contract c-08) holds the same object: same members in the
same order, enums and flag sets by name, GUIDs as text ("none" for the null GUID), keen::Time in seconds, strings only
when they are printable ASCII of at most 512 bytes, arrays of more than 16 bytes as "(N bytes)", variants with "$type",
impact configurations by their alias, "..." below depth 12. A read outside the blob is what a failed read is live: null.

The type graph is the game's own reflection read from the executable (tools/kfc/kfc/refl.py `extract`).
"""
import struct
import json
import time

# Kinds: the low byte of the reflection record's kind word (gamedata.h, refl.py).
K_BOOL, K_U8, K_S8, K_U16, K_S16, K_U32, K_S32, K_U64, K_S64, K_F32, K_F64 = range(1, 12)
K_ENUM, K_BM8, K_BM64, K_TYPEDEF, K_STRUCT, K_ARRAY = 0x0C, 0x0D, 0x10, 0x11, 0x12, 0x13
K_BLOBARRAY, K_BLOBSTRING, K_BLOBOPT, K_BLOBVAR, K_REF, K_GUID = 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D
_SIGNED = (K_S8, K_S16, K_S32, K_S64)

ROOT_DEPTH = 12  # gamedata_api.cpp t_get_json: node_json(root_of(section, e), 12)

# Read by C++, not by any program: config ids the live vocabulary names before any program does (gamedata_walk.cpp kBuiltin).
BUILTIN_CONFIGS = ((0xaf236ece, "ManaCost"), (0x727d7f78, "StaminaCost"), (0x2de5f1f5, "SpecialAbilityCost"),
                   (0xc8492400, "FuelAmount"), (0xe99aaff8, "FuelCharges"), (0x220e6b10, "UsedItem"))


def brief_of(q):
    """gamedata_types.cpp brief_of: "keen::ecs::LifeTime" -> "LifeTime"; templates keep their arguments."""
    lt = q.find("<")
    end = len(q) if lt < 0 else lt
    c = q.rfind("::", 0, end)
    return q if c < 0 else q[c + 2:]


def lower(s):
    """ASCII lower case, as game.cpp lower()."""
    return "".join(chr(ord(c) + 32) if "A" <= c <= "Z" else c for c in s)


def guid_text(b):
    """game.cpp guid_text: keen::Guid memory order {u32, u16, u16, u8[8]}."""
    d1, d2, d3 = struct.unpack_from("<IHH", b, 0)
    return f"{d1:08x}-{d2:04x}-{d3:04x}-{b[8]:02x}{b[9]:02x}-" + "".join(f"{x:02x}" for x in b[10:16])


def ref_text(b):
    return "none" if not any(b) else guid_text(b)


class Type:
    """One reflection record as gamedata_types.cpp's TType sees it."""
    __slots__ = ("name", "brief", "hash", "size", "count", "kind", "inner", "fields", "values", "_leaf")

    def __init__(self, rec):
        self.name = rec["name"]
        self.brief = brief_of(self.name)
        self.hash = rec["hash"]
        self.size = rec["size"]
        self.count = rec["count"]
        self.kind = rec["kind"]
        self.inner = rec.get("inner")  # a qualified name, or None
        self.fields = None              # [(name, type name, offset)], the base chain's first (filled by Graph)
        self.values = None              # enums: [(name, value as int64)]; bitmasks: their flag enum's
        self._leaf = False              # not computed yet


class Graph:
    """The type graph of one executable: a refl.py dump ({"types": [...]})."""

    def __init__(self, dump):
        if len(dump["types"]) > 100000 or sum(len(r.get("fields", [])) + len(r.get("values", [])) for r in dump["types"]) > 1000000:
            raise ValueError("boot reflection graph exceeds its budget")
        self.by_name = {}
        self._field_budget = 1000000
        self.by_hash = {}
        recs = {}
        for rec in dump["types"]:
            t = Type(rec)
            self.by_name[t.name] = t
            self.by_hash.setdefault(t.hash, t)
            recs[t.name] = rec
        self._recs = recs

    # ---- record access, as TType's lazy members ----
    def get(self, name):
        return self.by_name.get(name) if name else None

    def named(self, name):
        return self.by_name.get(name)

    def hashed(self, h):
        return self.by_hash.get(h)

    def inner(self, t):
        return self.get(t.inner) if t is not None else None

    def resolved(self, t):
        """Through typedefs (at most 12); None when the chain does not end."""
        for _ in range(12):
            if t is None or t.kind != K_TYPEDEF:
                break
            t = self.inner(t)
        return t if t is not None and t.kind != K_TYPEDEF else None

    def fields(self, t):
        """A struct's fields, its base chain's first (gamedata_types.cpp load: bases are inner records of kind struct,
        at most 16; fields without a name or at an offset of 2^24 or more are left out)."""
        if t.fields is None:
            out = []
            if t.kind == K_STRUCT:
                chain = []
                b = self.get(t.inner)
                while b is not None and b.kind == K_STRUCT and len(chain) < 16:
                    chain.append(b)
                    b = self.get(b.inner)
                for c in list(reversed(chain)) + [t]:
                    for f in self._recs[c.name].get("fields", []):
                        if f["name"] and f["offset"] < (1 << 24):
                            out.append((f["name"], f["type"], f["offset"]))
            self._field_budget -= len(out)
            if self._field_budget < 0:
                raise ValueError("boot reflection exceeds its inherited field budget")
            t.fields = out
        return t.fields

    def field(self, t, name):
        """TType::field: exact, then ignoring case. (name, type name, offset) or None."""
        fs = self.fields(t)
        for f in fs:
            if f[0] == name:
                return f
        ln = lower(name)
        for f in fs:
            if lower(f[0]) == ln:
                return f
        return None

    def enum_values(self, t):
        """enum_of: the values in record order, each as int64 (no values for more than 4096)."""
        if t.values is None:
            vals = []
            if t.kind == K_ENUM:
                rec = self._recs[t.name]
                if t.count <= 4096:
                    for v in rec.get("values", []):
                        x = v["value"] & 0xFFFFFFFFFFFFFFFF
                        vals.append((v["name"], x - (1 << 64) if x >= (1 << 63) else x))
            elif K_BM8 <= t.kind <= K_BM64:
                fe = self.get(t.inner)
                if fe is not None and fe.kind == K_ENUM:
                    vals = self.enum_values(fe)
            t.values = vals
        return t.values

    def leaf(self, decl):
        """gamedata_walk.cpp leaf_of: (vt, bytes, signed, offset, values) or None. vt: "bool", "int", "enum", "flags",
        "float", "time", "guid"."""
        if decl is None:
            return None
        if decl._leaf is not False:
            return decl._leaf
        decl._leaf = None  # a struct reaching itself is no leaf
        decl._leaf = self._leaf_of(decl)
        return decl._leaf

    def _leaf_of(self, decl):
        t = self.resolved(decl)
        if t is None:
            return None
        k, size = t.kind, t.size & 0xFF
        if K_BOOL <= k <= K_S64:
            if size not in (1, 2, 4, 8):
                return None
            return ("bool" if k == K_BOOL else "int", size, k in _SIGNED, 0, None)
        if k in (K_F32, K_F64):
            return ("float", size, True, 0, None) if size in (4, 8) else None
        if k == K_ENUM:
            if size not in (1, 2, 4, 8):
                return None
            u = self.resolved(self.inner(t))
            return ("enum", size, u is not None and u.kind in _SIGNED, 0, self.enum_values(t))
        if K_BM8 <= k <= K_BM64:
            if size not in (1, 2, 4, 8):
                return None
            return ("flags", size, False, 0, self.enum_values(t))
        if k in (K_REF, K_GUID):
            return ("guid", 16, False, 0, None) if t.size == 16 else None
        if k == K_STRUCT:
            if t.name == "keen::Time" and t.size == 8:
                return ("time", 8, True, 0, None)
            fs = self.fields(t)
            if len(fs) == 1:  # keen::HashKey32 {u32 value} and the like: the scalar
                it = self.get(fs[0][1])
                r = self.resolved(it)
                if r is not None and r.kind != K_STRUCT:
                    g = self.leaf(it)
                    if g is not None and g[0] != "guid":
                        return (g[0], g[1], g[2], fs[0][2], g[4])
        return None


class Blob:
    """A buffer and the reads gamedata_walk.cpp does on live memory (rd, rdv): None when outside the buffer."""
    __slots__ = ("b", "n")

    def __init__(self, b):
        self.b = b
        self.n = len(b)

    def ok(self, at, size):
        return 0 <= at and at + size <= self.n

    def u32(self, at):
        return struct.unpack_from("<I", self.b, at)[0] if self.ok(at, 4) else None

    def i32(self, at):
        return struct.unpack_from("<i", self.b, at)[0] if self.ok(at, 4) else None

    def bytes(self, at, size):
        return self.b[at:at + size] if self.ok(at, size) else None

    def blob_string(self, at):
        """game.cpp read_blob_string: "" unless {i32 rel != 0, u32 len 1..512} points at printable ASCII."""
        rel, n = self.i32(at), self.u32(at + 4)
        if rel is None or n is None or rel == 0 or n == 0 or n > 512:
            return ""
        s = self.bytes(at + rel, n)
        if s is None or any(c < 0x20 or c >= 0x7F for c in s):
            return ""
        return s.decode("ascii")


class ConfigNames:
    """The config vocabulary of gamedata_walk.cpp configs_build: the C++-read ids, then every impact program's variables
    (`dataLayout`: configId, dbgName up to "::", generic "impact_nodes." names left out). An id's name is its most used
    name that names no other id (ties: the first in byte order), else "0x%08x"."""

    def __init__(self):
        self._variables_left = 1000000
        self.names = {}   # id -> {name: uses}
        self.alias = {}   # lower(name) -> {ids}
        self.best = {}
        for i, n in BUILTIN_CONFIGS:
            self.add(i, n, 1000000)

    def add(self, cid, name, weight):
        self._variables_left -= 1
        if self._variables_left < 0:
            raise ValueError("boot impact vocabulary exceeds its variable budget")
        if not cid or not name:
            return
        self.alias.setdefault(lower(name), set()).add(cid)
        d = self.names.setdefault(cid, {})
        d[name] = d.get(name, 0) + weight

    def add_program(self, graph, blob):
        """One keen::ImpactProgram resource's variables."""
        prog, var = graph.named("keen::ImpactProgram"), graph.named("keen::ImpactVariable")
        if prog is None or var is None:
            return
        lay, vcid, vname = graph.field(prog, "dataLayout"), graph.field(var, "configId"), graph.field(var, "dbgName")
        if lay is None or vcid is None or vname is None:
            return
        b = Blob(blob)
        rel, n = b.i32(lay[2]), b.u32(lay[2] + 4)
        if rel is None or n is None or n > 1000000 or not rel:
            return
        data = lay[2] + rel
        for i in range(min(n, 4096)):
            v = data + i * var.size
            cid = b.u32(v + vcid[2])
            if not cid:
                continue
            nm = b.blob_string(v + vname[2])
            c = nm.find("::")
            if c >= 0:
                nm = nm[:c]
            if nm.startswith("impact_nodes."):
                nm = ""
            if nm:
                self.add(cid, nm, 1)

    def finish(self):
        self.best = {}
        for cid, names in self.names.items():
            best, uses = "", -1
            for n in sorted(names, key=lambda s: s.encode()):
                u = names[n]
                if len(self.alias[lower(n)]) == 1 and u > uses:
                    best, uses = n, u
            if best:
                self.best[cid] = best
        return self

    def name(self, cid):
        return self.best.get(cid) or f"0x{cid:08x}"


class Elements:
    """An array view: preserve the live reader's count limits without allocating a list of element addresses."""
    def __init__(self, el, at, count, stride):
        self.el, self.at, self.count, self.stride = el, at, count, stride

    def __len__(self):
        return self.count

    def __iter__(self):
        return ((self.el, self.at + i * self.stride) for i in range(self.count))


class Walker:
    """node_json over one blob."""

    def __init__(self, graph, configs, deadline=None):
        self.g = graph
        self.configs = configs
        self.deadline = deadline

    def json(self, type_name, blob, offset=0, depth=ROOT_DEPTH):
        self.nodes_left = 100000
        self.bytes_left = 4 * 1024 * 1024 - 256
        t = self.g.named(type_name) if isinstance(type_name, str) else type_name
        return self.node(t, Blob(blob), offset, depth)

    # ---- gamedata_walk.cpp ----
    def leaf_json(self, f, raw):
        vt, size, signed, _, values = f
        if vt == "bool":
            return raw[0] != 0
        if vt in ("int", "enum", "flags"):
            # refl.cpp decode: v.i is the value as int64, sign-extended when the type is signed
            i = int.from_bytes(raw[:size], "little", signed=signed)
            if i >= 1 << 63:
                i -= 1 << 64
            if vt == "enum" and values:
                for n, x in values:
                    if x == i:
                        return n
            if vt == "flags" and values:
                a, rest = [], i & 0xFFFFFFFFFFFFFFFF
                for n, x in values:
                    if 0 <= x < 64 and (rest >> x) & 1:
                        a.append(n)
                        rest &= ~(1 << x)
                if rest:
                    a.append(rest)
                return a
            if not signed and size == 8:
                return i & 0xFFFFFFFFFFFFFFFF
            return i
        if vt == "float":
            v = struct.unpack_from("<f" if size == 4 else "<d", raw, 0)[0]
            return v if v == v and v not in (float("inf"), float("-inf")) else None  # nlohmann writes NaN/inf as null
        if vt == "time":
            return int.from_bytes(raw[:8], "little", signed=True) / 1e9
        if vt == "guid":
            return ref_text(raw[:16])
        return None

    def deref(self, t, b, at):
        for _ in range(6):
            if t is None:
                break
            if t.kind == K_TYPEDEF:
                t = self.g.resolved(t)
                continue
            if t.kind == K_BLOBVAR:
                h, rel, size = b.u32(at), b.i32(at + 4), b.u32(at + 8)
                if h is None or rel is None or size is None or not h:
                    return None, 0
                vt = self.g.hashed(h)
                t, at = (self.g.resolved(vt) if vt is not None else None), at + 4 + rel
                continue
            if t.kind == K_BLOBOPT:
                rel = b.i32(at)
                if not rel:
                    return None, 0
                inner = self.g.inner(t)
                t, at = (self.g.resolved(inner) if inner is not None else None), at + rel
                continue
            break
        return t, at

    def elements(self, t, b, at):
        el = self.g.inner(t)
        if el is None:
            return None
        er = self.g.resolved(el)
        if er is None:
            return None
        if t.kind == K_ARRAY:
            es = er.size or (t.size // t.count if t.count else 0)
            return Elements(el, at, min(t.count, 4096), es)
        if t.kind == K_BLOBARRAY:
            rel, n = b.i32(at), b.u32(at + 4)
            if rel is None or n is None or n > 1000000:
                return None
            count = n if rel else 0
            data = at + rel if rel and n else 0
            es = er.size or 12
            return Elements(el, data, min(count, 100000), es)
        return None

    def node(self, t, b, at, depth):
        self.nodes_left -= 1
        if self.nodes_left < 0:
            raise ValueError("boot JSON exceeds its node budget")
        if self.deadline is not None and self.nodes_left % 1024 == 0 and time.monotonic() >= self.deadline:
            raise TimeoutError("boot snapshot exceeded its ten-minute decoding budget")
        v = self._node(t, b, at, depth)
        if isinstance(v, dict):
            size = 2 + len(v) + sum(len(json.dumps(k, ensure_ascii=False).encode("utf-8")) + 1 for k in v)
        elif isinstance(v, list):
            size = 2 + len(v)
        else:
            size = len(json.dumps(v, ensure_ascii=False).encode("utf-8"))
        self.bytes_left -= size
        if self.bytes_left < 0:
            raise ValueError("boot JSON exceeds its byte budget")
        return v

    def _node(self, t, b, at, depth):
        r = self.g.resolved(t) if t is not None else None
        if r is not None and r.kind != K_BLOBVAR:
            f = self.g.leaf(t)
            if f is not None:
                raw = b.bytes(at + f[3], f[1])
                return None if raw is None else self.leaf_json(f, raw)
        if depth <= 0:
            return "..."
        if r is not None and r.kind == K_BLOBSTRING:
            return b.blob_string(at)
        nt, nat = self.deref(t, b, at)
        if nt is None:
            return None
        if nt.kind == K_BLOBSTRING:
            return b.blob_string(nat)
        if nt.kind == K_STRUCT:
            o = {}
            if r is not None and r.kind == K_BLOBVAR:
                o["$type"] = nt.brief
            if nt.name == "keen::impact::Configurations":
                return self.configurations(o, nt, b, nat, depth)
            for name, ftype, off in self.g.fields(nt):
                o[name] = self.node(self.g.get(ftype), b, nat + off, depth - 1)
            return o
        if nt.kind in (K_ARRAY, K_BLOBARRAY):
            els = self.elements(nt, b, nat)
            if els is None:
                return None
            er = self.g.resolved(self.g.inner(nt))
            if er is not None and er.kind == K_U8 and len(els) > 16:
                return f"({len(els)} bytes)"
            return [self.node(et, b, eat, depth - 1) for et, eat in els]
        return None

    def configurations(self, o, nt, b, at, depth):
        for arr in ("simple", "scaled"):
            af = self.g.field(nt, arr)
            at_t = self.g.resolved(self.g.get(af[1])) if af is not None else None
            els = self.elements(at_t, b, at + af[2]) if at_t is not None else None
            if els is None:
                continue
            for et, eat in els:
                ct, cat = self.deref(et, b, eat)
                if ct is None:
                    continue
                idf = self.g.field(ct, "configId")
                cid = (b.u32(cat + idf[2]) or 0) if idf is not None else 0
                vf = self.g.field(ct, "value")
                v = self.node(self.g.get(vf[1]), b, cat + vf[2], depth - 1) if vf is not None else None
                if arr == "scaled":
                    s = {"value": v}
                    for k in ("scaleFactor", "function", "source"):
                        sf = self.g.field(ct, k)
                        if sf is not None:
                            s[k] = self.node(self.g.get(sf[1]), b, cat + sf[2], depth - 1)
                    v = s
                o[self.configs.name(cid)] = {"$config": ct.brief, "value": v}
        return o
