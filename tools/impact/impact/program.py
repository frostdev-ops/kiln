# SPDX-License-Identifier: MIT
"""keen::ImpactProgram: the model and a byte-exact codec that needs no game files.

Layout (the game's reflection, svn 1285601; kfc's names; type 0xd4922d88, layout hash 0x4daf7389):

    keen::ImpactProgram (64 B, align 4)
      +0x00 id            ProgramId (HashKey32) = FNV-1a-32 of the program GUID's 16 bytes
      +0x04 programGuid   Guid (16 B, keen::Guid memory order)
      +0x14 stackSize     u16 (256 in all 571 vanilla programs; the instance stack is 32 slots regardless)
      +0x16 usedStreams   Bitmask8<impact::EventStreamType> (OnCollision 0, OnHit 1, OnParry 2, EntityInRange 3)
      +0x18 code          BlobArray<ImpactCommand = u32>
      +0x20 codeShutdown  BlobArray<u32>
      +0x28 dataLayout    BlobArray<ImpactVariable>
      +0x30 data          BlobArray<u8>     the initial instance data (variables live at their offsetInBytes)
      +0x38 queryCallIds  BlobArray<u32>    ECALL site ids of query natives (per-instance query state)

    keen::ImpactVariable (24 B, align 4; layout hash 0xdcf25e05)
      +0x00 name          HashKey32 (opaque id; COPY finds variables by it)
      +0x04 configId      HashKey32 (0 = not config-bound)
      +0x08 type          HashKey32 = FNV-1a-32 of the type's impact name ("float32", "impact.DamageSetup", ...)
      +0x0C size          u16
      +0x0E offsetInBytes u16
      +0x10 dbgName       BlobString

A BlobArray / BlobString is {i32 offset relative to the field itself, u32 count}; empty = {0, 0}. The blob is the
root struct followed by the out-of-line blocks in field order, each aligned to its element type (u32: 4, variables:
4, the dbgName strings: 1, right after the variable block, data: 1). This is tools/kfc's blob rule; all 571 vanilla
programs re-encode byte for byte with it.
"""
import struct
import uuid

TYPE_NAME = "keen::ImpactProgram"
TYPE_HASH = 0xD4922D88
LAYOUT_HASH = 0x4DAF7389
VAR_TYPE_NAME = "keen::ImpactVariable"
VAR_LAYOUT_HASH = 0xDCF25E05
ROOT_SIZE = 64
VAR_SIZE = 24

STREAMS = ("OnCollision", "OnHit", "OnParry", "EntityInRange")

# the field layout this codec assumes, checked against a TypeDb by layout_check()
EXPECTED_FIELDS = {
    TYPE_NAME: [("id", 0x00), ("programGuid", 0x04), ("stackSize", 0x14), ("usedStreams", 0x16), ("code", 0x18),
                ("codeShutdown", 0x20), ("dataLayout", 0x28), ("data", 0x30), ("queryCallIds", 0x38)],
    VAR_TYPE_NAME: [("name", 0x00), ("configId", 0x04), ("type", 0x08), ("size", 0x0C), ("offsetInBytes", 0x0E),
                    ("dbgName", 0x10)],
}


def fnv1a32(data, seed=0x811C9DC5):
    if isinstance(data, str):
        data = data.encode()
    h = seed
    for c in data:
        h = ((h ^ c) * 0x01000193) & 0xFFFFFFFF
    return h


def guid_bytes(text):
    return uuid.UUID(str(text)).bytes_le


def guid_text(b):
    return str(uuid.UUID(bytes_le=bytes(b)))


def program_id(guid):
    """ProgramId = FNV-1a-32 of the GUID's 16 bytes (the rule of impact_initialize 0x197250 and every vanilla id)."""
    return fnv1a32(guid_bytes(guid))


class CodecError(Exception):
    pass


class Variable:
    __slots__ = ("name", "config", "type", "size", "offset", "dbg")

    def __init__(self, name=0, config=0, type=0, size=0, offset=0, dbg=""):
        self.name, self.config, self.type, self.size, self.offset, self.dbg = name, config, type, size, offset, dbg

    def copy(self):
        return Variable(self.name, self.config, self.type, self.size, self.offset, self.dbg)

    def __eq__(self, o):
        return isinstance(o, Variable) and all(getattr(self, k) == getattr(o, k) for k in self.__slots__)

    def __repr__(self):
        return (f"Variable({self.dbg!r} type {self.type:#x} @{self.offset} size {self.size}"
                + (f" config {self.config:#x}" if self.config else "") + ")")


class Program:
    def __init__(self, guid, id=None, stack_size=256, streams=0, code=None, shutdown=None, variables=None,
                 data=b"", query_ids=None):
        self.guid = guid_text(guid_bytes(guid))
        self.id = program_id(self.guid) if id is None else id
        self.stack_size = stack_size
        self.streams = streams
        self.code = list(code or [])
        self.shutdown = list(shutdown or [])
        self.variables = list(variables or [])
        self.data = bytes(data)
        self.query_ids = list(query_ids or [])

    def copy(self):
        return Program(self.guid, self.id, self.stack_size, self.streams, self.code, self.shutdown,
                       [v.copy() for v in self.variables], self.data, self.query_ids)

    def __eq__(self, o):
        return isinstance(o, Program) and all(
            getattr(self, k) == getattr(o, k) for k in
            ("guid", "id", "stack_size", "streams", "code", "shutdown", "variables", "data", "query_ids"))

    @property
    def stream_names(self):
        return [STREAMS[i] if i < len(STREAMS) else str(i) for i in range(8) if (self.streams >> i) & 1]

    def data_extent(self):
        return max([v.offset + v.size for v in self.variables] or [0])

    def var_index(self, dbg):
        for i, v in enumerate(self.variables):
            if v.dbg == dbg:
                return i
        return None


# ---------------------------------------------------------------- decode
def _arr(b, o):
    rel, n = struct.unpack_from("<iI", b, o)
    if n == 0:
        return 0, 0
    at = o + rel
    if at < 0 or at > len(b):
        raise CodecError(f"array at {o:#x} points outside the blob ({at:#x})")
    return at, n


def decode(blob):
    b = bytes(blob)
    if len(b) < ROOT_SIZE:
        raise CodecError(f"{len(b)} bytes: smaller than an ImpactProgram (64)")
    pid = struct.unpack_from("<I", b, 0)[0]
    guid = guid_text(b[4:20])
    stack_size, streams = struct.unpack_from("<HB", b, 0x14)

    def u32s(o):
        at, n = _arr(b, o)
        if at + 4 * n > len(b):
            raise CodecError(f"u32 array at {o:#x}: {n} elements past the end")
        return list(struct.unpack_from(f"<{n}I", b, at)) if n else []

    code, shutdown = u32s(0x18), u32s(0x20)
    at, n = _arr(b, 0x28)
    if at + VAR_SIZE * n > len(b):
        raise CodecError(f"dataLayout: {n} variables past the end")
    variables = []
    for i in range(n):
        e = at + VAR_SIZE * i
        name, cfg, typ, size, off = struct.unpack_from("<IIIHH", b, e)
        so, sn = _arr(b, e + 0x10)
        if so + sn > len(b):
            raise CodecError(f"variable {i}: dbgName past the end")
        variables.append(Variable(name, cfg, typ, size, off, b[so:so + sn].decode("utf-8", "surrogateescape")))
    dat, dn = _arr(b, 0x30)
    if dat + dn > len(b):
        raise CodecError("data past the end")
    data = b[dat:dat + dn]
    q = u32s(0x38)
    p = Program(guid, pid, stack_size, streams, code, shutdown, variables, data, q)
    return p


# ---------------------------------------------------------------- encode
def encode(p):
    buf = bytearray(ROOT_SIZE)

    def reserve(size, align):
        o = (len(buf) + align - 1) // align * align
        buf.extend(b"\0" * (o - len(buf) + size))
        return o

    def put_arr(field, at, n):
        struct.pack_into("<iI", buf, field, (at - field) if n else 0, n)

    struct.pack_into("<I", buf, 0, p.id & 0xFFFFFFFF)
    buf[4:20] = guid_bytes(p.guid)
    struct.pack_into("<HB", buf, 0x14, p.stack_size & 0xFFFF, p.streams & 0xFF)
    for field, words in ((0x18, p.code), (0x20, p.shutdown)):
        if words:
            at = reserve(4 * len(words), 4)
            struct.pack_into(f"<{len(words)}I", buf, at, *[w & 0xFFFFFFFF for w in words])
            put_arr(field, at, len(words))
        else:
            put_arr(field, 0, 0)
    if p.variables:
        at = reserve(VAR_SIZE * len(p.variables), 4)
        put_arr(0x28, at, len(p.variables))
        for i, v in enumerate(p.variables):
            e = at + VAR_SIZE * i
            if not (0 <= v.size <= 0xFFFF and 0 <= v.offset <= 0xFFFF):
                raise CodecError(f"variable {v.dbg!r}: size/offset out of u16 range")
            struct.pack_into("<IIIHH", buf, e, v.name & 0xFFFFFFFF, v.config & 0xFFFFFFFF, v.type & 0xFFFFFFFF,
                             v.size, v.offset)
            s = v.dbg.encode("utf-8", "surrogateescape")
            if s:
                so = reserve(len(s), 1)
                buf[so:so + len(s)] = s
                put_arr(e + 0x10, so, len(s))
            else:
                put_arr(e + 0x10, 0, 0)
    else:
        put_arr(0x28, 0, 0)
    if p.data:
        at = reserve(len(p.data), 1)
        buf[at:at + len(p.data)] = p.data
        put_arr(0x30, at, len(p.data))
    else:
        put_arr(0x30, 0, 0)
    if p.query_ids:
        at = reserve(4 * len(p.query_ids), 4)
        struct.pack_into(f"<{len(p.query_ids)}I", buf, at, *p.query_ids)
        put_arr(0x38, at, len(p.query_ids))
    else:
        put_arr(0x38, 0, 0)
    return bytes(buf)


def layout_check(typedb):
    """Compare this codec's assumed layout with a TypeDb (tools/kfc, from the game's exe). Returns a list of
    problems ([] = the codec matches this game build)."""
    probs = []
    for tname, lh in ((TYPE_NAME, LAYOUT_HASH), (VAR_TYPE_NAME, VAR_LAYOUT_HASH)):
        t = typedb.get(tname)
        if t is None:
            probs.append(f"{tname}: not in the type db")
            continue
        if t["layout"] != lh:
            probs.append(f"{tname}: layout hash {t['layout']:#x}, this codec knows {lh:#x}")
        got = [(f["name"], f["offset"]) for f in typedb.all_fields(t)]
        if got != EXPECTED_FIELDS[tname]:
            probs.append(f"{tname}: fields {got} differ from {EXPECTED_FIELDS[tname]}")
        want_size = ROOT_SIZE if tname == TYPE_NAME else VAR_SIZE
        if t["size"] != want_size:
            probs.append(f"{tname}: size {t['size']} != {want_size}")
    return probs
