# SPDX-License-Identifier: MIT
"""A SPIR-V interpreter for compute shaders, enough to run VFX particle modules on the CPU, one invocation at a time.

It runs the compiled module, not a port of its source, so it checks what would reach the driver. An access outside a
buffer raises `BufferFault`: the game's robust buffer access would hide such a bug, this makes it a failure.

It says nothing about the GPU: not about timing, memory ordering, driver bugs, or whether anything draws correctly.
"""
import math
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[5] / "tools/kfc"))

from kfc.spirv import Module  # noqa: E402

U32 = 0xFFFFFFFF


class BufferFault(Exception):
    pass


class Unsupported(Exception):
    pass


def f32(x):
    """Round a Python float to float32."""
    try:
        return struct.unpack("<f", struct.pack("<f", x))[0]
    except OverflowError:
        return math.inf if x > 0 else -math.inf


def f2u(x):
    return struct.unpack("<I", struct.pack("<f", f32(x)))[0]


def u2f(x):
    return struct.unpack("<f", struct.pack("<I", x & U32))[0]


def signed(x):
    x &= U32
    return x - (1 << 32) if x >= (1 << 31) else x


def half_bits(f):
    """float32 -> IEEE half, round to nearest (packHalf2x16)."""
    try:
        return struct.unpack("<H", struct.pack("<e", f))[0]
    except OverflowError:
        return 0x7C00 if f > 0 else 0xFC00


class Buffer:
    def __init__(self, name, size):
        self.name = name
        self.data = bytearray(size)

    def _check(self, off, n):
        if off < 0 or off + n > len(self.data):
            raise BufferFault(f"{self.name}: {n} bytes at {off}, size {len(self.data)}")

    def u32(self, off):
        self._check(off, 4)
        return struct.unpack_from("<I", self.data, off)[0]

    def put_u32(self, off, v):
        self._check(off, 4)
        struct.pack_into("<I", self.data, off, v & U32)

    def f32(self, off):
        return u2f(self.u32(off))

    def put_f32(self, off, v):
        self.put_u32(off, f2u(v))


class Ptr:
    __slots__ = ("kind", "buf", "off", "ty", "path", "var")

    def __init__(self, kind, ty, buf=None, off=0, var=None, path=()):
        self.kind = kind    # "mem" or "local"
        self.ty = ty
        self.buf = buf
        self.off = off
        self.var = var
        self.path = path


class Sim:
    """One module. `bindings` maps (set, binding) to a Buffer; `pc` is the push constant Buffer."""

    def __init__(self, data):
        self.raw = bytes(data)
        self.m = Module(data)
        w = struct.unpack(f"<{len(data) // 4}I", bytes(data))
        self.ins = []
        i = 5
        while i < len(w):
            op = w[i] & 0xFFFF
            wc = w[i] >> 16
            self.ins.append((op, w[i + 1:i + wc]))
            i += wc
        self.const = {}
        self.fn = []
        self.labels = {}
        self.ext = set()
        self.locals_decl = []      # the entry function's
        self.privates = []         # Private globals: (pointer type, id, initialiser or None)
        self.funcs = {}            # function id -> [first instruction, parameter ids, local declarations]
        self.builtins = {}
        for v in self.m.variables:
            d = self.m.dec.get(v, {})
            if 11 in d:
                self.builtins[v] = d[11]
        entry = next((a[1] for op, a in self.ins if op == 15), None)   # OpEntryPoint
        cur = None
        for op, a in self.ins:
            if op == 54:
                cur = [len(self.fn), [], []]
                self.funcs[a[1]] = cur
                continue
            if op == 56:
                cur = None
                continue
            if cur is not None:
                if op == 55:
                    cur[1].append(a[1])
                    continue
                if op == 248:
                    self.labels[a[0]] = len(self.fn)
                if op == 59:
                    cur[2].append(a)
                    continue
                self.fn.append((op, a))
            else:
                self._global(op, a)
        self.entry = entry
        self.locals_decl = self.funcs[entry][2] if entry in self.funcs else []
        self.steps = 0

    def _global(self, op, a):
        if op == 43:      # OpConstant
            ty = self.m.types[a[0]]
            self.const[a[1]] = u2f(a[2]) if ty[0] == "float" else a[2]
        elif op == 41:
            self.const[a[1]] = True
        elif op == 42:
            self.const[a[1]] = False
        elif op == 44:    # OpConstantComposite
            self.const[a[1]] = tuple(self.const[x] for x in a[2:])
        elif op == 46:    # OpConstantNull
            self.const[a[1]] = self._null(a[0])
        elif op == 1:     # OpUndef
            self.const[a[1]] = self._null(a[0])
        elif op == 11:    # OpExtInstImport
            self.ext.add(a[0])
        elif op == 59 and a[2] == 6:   # a Private variable
            self.privates.append((a[0], a[1], a[3] if len(a) > 3 else None))

    def _null(self, t):
        ty = self.m.types[t]
        k = ty[0]
        if k == "float":
            return 0.0
        if k == "int":
            return 0
        if k == "bool":
            return False
        if k == "vec":
            return tuple(self._null(ty[1]) for _ in range(ty[2]))
        if k == "struct":
            return tuple(self._null(x) for x in ty[1])
        if k == "array":
            return tuple(self._null(ty[1]) for _ in range(self.m.constants[ty[2]]))
        raise Unsupported(f"null of {ty}")

    # ---- typed memory ------------------------------------------------------------------------------------------------
    def _load_mem(self, buf, off, t):
        ty = self.m.types[t]
        k = ty[0]
        if k == "int":
            return buf.u32(off)
        if k == "float":
            return buf.f32(off)
        if k == "bool":
            return buf.u32(off) != 0
        if k == "vec":
            return tuple(self._load_mem(buf, off + 4 * i, ty[1]) for i in range(ty[2]))
        if k == "struct":
            return tuple(self._load_mem(buf, off + self.m.member_dec[(t, i)][35], m) for i, m in enumerate(ty[1]))
        if k == "array":
            n = self.m.constants[ty[2]]
            s = self.m.dec[t][6]
            return tuple(self._load_mem(buf, off + s * i, ty[1]) for i in range(n))
        raise Unsupported(f"load {ty}")

    def _store_mem(self, buf, off, t, v):
        ty = self.m.types[t]
        k = ty[0]
        if k == "int":
            buf.put_u32(off, v)
        elif k == "float":
            buf.put_f32(off, v)
        elif k == "bool":
            buf.put_u32(off, 1 if v else 0)
        elif k == "vec":
            for i in range(ty[2]):
                self._store_mem(buf, off + 4 * i, ty[1], v[i])
        elif k == "struct":
            for i, m in enumerate(ty[1]):
                self._store_mem(buf, off + self.m.member_dec[(t, i)][35], m, v[i])
        elif k == "array":
            n = self.m.constants[ty[2]]
            s = self.m.dec[t][6]
            for i in range(n):
                self._store_mem(buf, off + s * i, ty[1], v[i])
        else:
            raise Unsupported(f"store {ty}")

    def _chain(self, p, idx, vals):
        t = p.ty
        if p.kind == "mem":
            off = p.off
            for x in idx:
                ty = self.m.types[t]
                i = vals(x)
                if ty[0] == "struct":
                    off += self.m.member_dec[(t, i)][35]
                    t = ty[1][i]
                elif ty[0] in ("array", "rtarray"):
                    off += self.m.dec[t][6] * i
                    t = ty[1]
                elif ty[0] == "vec":
                    off += 4 * i
                    t = ty[1]
                else:
                    raise Unsupported(f"chain through {ty}")
            return Ptr("mem", t, buf=p.buf, off=off)
        path = list(p.path)
        for x in idx:
            ty = self.m.types[t]
            i = vals(x)
            path.append(i)
            if ty[0] == "struct":
                t = ty[1][i]
            elif ty[0] in ("array", "vec"):
                t = ty[1]
            else:
                raise Unsupported(f"local chain through {ty}")
        return Ptr("local", t, var=p.var, path=tuple(path))

    def _local_get(self, p, frame):
        v = frame[p.var]
        for i in p.path:
            v = v[i]
        return v

    def _local_set(self, p, frame, val):
        if not p.path:
            frame[p.var] = val
            return

        def rec(v, path):
            v = list(v)
            if len(path) == 1:
                v[path[0]] = val
            else:
                v[path[0]] = rec(v[path[0]], path[1:])
            return tuple(v)

        frame[p.var] = rec(frame[p.var], p.path)

    # ---- running -----------------------------------------------------------------------------------------------------
    def run(self, bindings, pc, global_id, subgroup_size=32, max_steps=2_000_000):
        """Run one invocation."""
        vals = {}
        frame = {}
        roots = {}
        for v, (pt, sc) in self.m.variables.items():
            t = self.m.types[pt][2]
            d = self.m.dec.get(v, {})
            if sc in (2, 12):   # Uniform, StorageBuffer
                sb = (d.get(34, 0), d.get(33, 0))
                if sb not in bindings:
                    raise BufferFault(f"binding {sb} has no buffer")
                roots[v] = Ptr("mem", t, buf=bindings[sb], off=0)
            elif sc == 9:       # PushConstant
                roots[v] = Ptr("mem", t, buf=pc, off=0)
            elif sc == 1:       # Input builtin
                b = self.builtins.get(v)
                if b == 28:
                    frame[v] = (global_id, 0, 0)
                elif b == 36:
                    frame[v] = subgroup_size
                elif b == 29:
                    frame[v] = global_id % 32
                else:
                    raise Unsupported(f"builtin {b}")
                roots[v] = Ptr("local", t, var=v)
        for pt, rid, init in self.privates:
            t = self.m.types[pt][2]
            frame[rid] = self.const[init] if init is not None else self._null(t)
            roots[rid] = Ptr("local", t, var=rid)

        def declare(decls):
            # a call's locals start fresh (SPIR-V for shaders has no recursion, so one slot per variable is enough)
            for a in decls:
                pt, rid = a[0], a[1]
                t = self.m.types[pt][2]
                frame[rid] = self.const[a[3]] if len(a) > 3 else self._null(t)
                roots[rid] = Ptr("local", t, var=rid)

        declare(self.locals_decl)
        calls = []   # (return pc, prev, cur, result id)

        def val(x):
            if x in vals:
                return vals[x]
            if x in self.const:
                return self.const[x]
            if x in roots:
                return roots[x]
            raise Unsupported(f"value %{x}")

        fn = self.fn
        pc_i = self.funcs[self.entry][0] if self.entry in self.funcs else 0
        prev = cur = None
        steps = 0
        GLSL = self._glsl
        while True:
            op, a = fn[pc_i]
            pc_i += 1
            steps += 1
            if steps > max_steps:
                raise BufferFault("step limit: an invocation did not end")
            if op == 248:
                prev, cur = cur, a[0]
            elif op == 245:     # OpPhi: read all incoming in one go is not needed for this use, but must use prev
                for i in range(2, len(a), 2):
                    if a[i + 1] == prev:
                        vals[a[1]] = val(a[i])
                        break
                else:
                    raise Unsupported("phi without an edge")
            elif op in (247, 246, 8, 224, 225):  # merges, line, barriers
                pass
            elif op == 249:
                pc_i = self.labels[a[0]]
            elif op == 250:
                pc_i = self.labels[a[1] if val(a[0]) else a[2]]
            elif op == 251:
                s = val(a[0])
                tgt = a[1]
                for i in range(2, len(a), 2):
                    if a[i] == s:
                        tgt = a[i + 1]
                        break
                pc_i = self.labels[tgt]
            elif op == 253 or op == 254:   # OpReturn, OpReturnValue
                if not calls:
                    self.steps += steps
                    return
                ret = val(a[0]) if op == 254 else None
                pc_i, prev, cur, rid = calls.pop()
                if op == 254:
                    vals[rid] = ret
            elif op == 57:      # OpFunctionCall
                start, params, decls = self.funcs[a[2]]
                args_ = [val(x) for x in a[3:]]
                calls.append((pc_i, prev, cur, a[1]))
                for pid, v in zip(params, args_):
                    vals[pid] = v
                declare(decls)
                pc_i = start
                prev = cur = None
            elif op == 61:      # OpLoad
                p = val(a[2])
                vals[a[1]] = self._load_mem(p.buf, p.off, p.ty) if p.kind == "mem" else self._local_get(p, frame)
            elif op == 62:      # OpStore
                p = val(a[0])
                if p.kind == "mem":
                    self._store_mem(p.buf, p.off, p.ty, val(a[1]))
                else:
                    self._local_set(p, frame, val(a[1]))
            elif op in (65, 66):
                vals[a[1]] = self._chain(val(a[2]), a[3:], val)
            elif op == 81:      # OpCompositeExtract
                v = val(a[2])
                for i in a[3:]:
                    v = v[i]
                vals[a[1]] = v
            elif op == 82:      # OpCompositeInsert
                vals[a[1]] = self._insert(val(a[3]), a[4:], val(a[2]))
            elif op == 80:
                parts = []
                for x in a[2:]:
                    v = val(x)
                    if isinstance(v, tuple) and self.m.types[a[0]][0] == "vec":
                        parts.extend(v)
                    else:
                        parts.append(v)
                vals[a[1]] = tuple(parts)
            elif op == 79:      # OpVectorShuffle
                x, y = val(a[2]), val(a[3])
                all_ = tuple(x) + tuple(y)
                vals[a[1]] = tuple(all_[i] if i != U32 else 0 for i in a[4:])
            elif op in (83, 400):
                vals[a[1]] = val(a[2])
            elif op == 124:     # OpBitcast
                vals[a[1]] = self._bitcast(a[0], val(a[2]))
            elif op == 12:      # OpExtInst
                vals[a[1]] = GLSL(a[3], [val(x) for x in a[4:]], a[0])
            elif op in (232, 233, 234, 235, 240, 241, 242):  # atomics on a word
                p = val(a[2])
                old = p.buf.u32(p.off) if p.kind == "mem" else self._local_get(p, frame)
                if op == 232:
                    new = old + 1
                elif op == 233:
                    new = old - 1
                else:
                    v = val(a[5])
                    new = {234: old + v, 235: old - v, 240: old & v, 241: old | v, 242: old ^ v}[op]
                p.buf.put_u32(p.off, new & U32)
                vals[a[1]] = old
            elif op == 227:
                p = val(a[2])
                vals[a[1]] = p.buf.u32(p.off)
            elif op == 228:
                p = val(a[0])
                p.buf.put_u32(p.off, val(a[3]))
            else:
                r = self._alu(op, a, val)
                if r is NotImplemented:
                    raise Unsupported(f"opcode {op}")
                vals[a[1]] = r

    @staticmethod
    def _insert(comp, path, obj):
        v = list(comp)
        if len(path) == 1:
            v[path[0]] = obj
        else:
            v[path[0]] = Sim._insert(v[path[0]], path[1:], obj)
        return tuple(v)

    def _bitcast(self, t, v):
        ty = self.m.types[t]
        if ty[0] == "vec":
            return tuple(self._bitcast(ty[1], x) for x in v)
        if ty[0] == "float":
            return u2f(v) if isinstance(v, int) else v
        if ty[0] == "int":
            return f2u(v) if isinstance(v, float) else v & U32
        raise Unsupported(f"bitcast to {ty}")

    def _alu(self, op, a, val):
        def bin_(f):
            x, y = val(a[2]), val(a[3])
            if isinstance(x, tuple):
                if isinstance(y, tuple):
                    return tuple(f(p, q) for p, q in zip(x, y))
                return tuple(f(p, y) for p in x)
            return f(x, y)

        def un(f):
            x = val(a[2])
            return tuple(f(p) for p in x) if isinstance(x, tuple) else f(x)

        fdiv = lambda p, q: (p / q if q != 0.0 else (math.nan if p == 0.0 or p != p else math.copysign(math.inf, p) * math.copysign(1.0, q)))
        table2 = {
            128: lambda p, q: (p + q) & U32, 130: lambda p, q: (p - q) & U32, 132: lambda p, q: (p * q) & U32,
            134: lambda p, q: (p // q) if q else 0, 137: lambda p, q: (p % q) if q else 0,
            129: lambda p, q: f32(p + q), 131: lambda p, q: f32(p - q), 133: lambda p, q: f32(p * q), 136: fdiv,
            194: lambda p, q: p >> (q & 31), 196: lambda p, q: (p << (q & 31)) & U32,
            195: lambda p, q: (signed(p) >> (q & 31)) & U32,
            197: lambda p, q: p | q, 198: lambda p, q: p ^ q, 199: lambda p, q: p & q,
            170: lambda p, q: p == q, 171: lambda p, q: p != q,
            172: lambda p, q: p > q, 173: lambda p, q: signed(p) > signed(q),
            174: lambda p, q: p >= q, 175: lambda p, q: signed(p) >= signed(q),
            176: lambda p, q: p < q, 177: lambda p, q: signed(p) < signed(q),
            178: lambda p, q: p <= q, 179: lambda p, q: signed(p) <= signed(q),
            180: lambda p, q: p == q, 181: lambda p, q: p == q or p != p or q != q,
            182: lambda p, q: p != q and p == p and q == q, 183: lambda p, q: p != q,
            184: lambda p, q: p < q, 185: lambda p, q: p < q or p != p or q != q,
            186: lambda p, q: p > q, 187: lambda p, q: p > q or p != p or q != q,
            188: lambda p, q: p <= q, 189: lambda p, q: p <= q or p != p or q != q,
            190: lambda p, q: p >= q, 191: lambda p, q: p >= q or p != p or q != q,
            164: lambda p, q: p == q, 165: lambda p, q: p != q,
            166: lambda p, q: p or q, 167: lambda p, q: p and q,
        }
        if op in table2:
            return bin_(table2[op])
        if op == 126:
            return un(lambda p: (-p) & U32)
        if op == 127:
            return un(lambda p: -p)
        if op == 168:
            return un(lambda p: not p)
        if op == 200:
            return un(lambda p: (~p) & U32)
        if op == 109:   # ConvertFToU
            return un(lambda p: (int(p) & U32) if p == p and abs(p) != math.inf else 0)
        if op == 110:   # ConvertFToS
            return un(lambda p: (int(p) & U32) if p == p and abs(p) != math.inf else 0)
        if op == 111:
            return un(lambda p: f32(float(signed(p))))
        if op == 112:
            return un(lambda p: f32(float(p)))
        if op == 142:   # VectorTimesScalar
            s = val(a[3])
            return tuple(f32(p * s) for p in val(a[2]))
        if op == 148:   # Dot
            return f32(sum(p * q for p, q in zip(val(a[2]), val(a[3]))))
        if op == 169:   # Select
            c, x, y = val(a[2]), val(a[3]), val(a[4])
            if isinstance(c, tuple):
                return tuple(p if s else q for s, p, q in zip(c, x, y))
            return x if c else y
        if op == 154:
            x = val(a[2])
            return any(x) if isinstance(x, tuple) else bool(x)
        if op == 155:
            x = val(a[2])
            return all(x) if isinstance(x, tuple) else bool(x)
        if op == 156:   # IsNan
            return un(lambda p: p != p)
        if op == 157:   # IsInf
            return un(lambda p: abs(p) == math.inf)
        return NotImplemented

    def _glsl(self, n, x, t):
        def map1(f):
            return tuple(f(p) for p in x[0]) if isinstance(x[0], tuple) else f(x[0])

        def map2(f):
            if isinstance(x[0], tuple):
                return tuple(f(p, q) for p, q in zip(x[0], x[1]))
            return f(x[0], x[1])

        def map3(f):
            if isinstance(x[0], tuple):
                return tuple(f(p, q, r) for p, q, r in zip(x[0], x[1], x[2]))
            return f(x[0], x[1], x[2])

        def fsafe(f):
            def g(*v):
                try:
                    return f32(f(*v))
                except (ValueError, OverflowError, ZeroDivisionError):
                    return math.nan
            return g

        def round_even(p):
            return round(p) * 1.0 if p == p and abs(p) != math.inf else p

        def nmin(p, q):
            return q if p != p else (p if q != q else min(p, q))

        def nmax(p, q):
            return q if p != p else (p if q != q else max(p, q))

        if n in (1, 2):
            return map1(round_even)
        if n == 3:
            return map1(fsafe(math.trunc))
        if n == 4:
            return map1(abs)
        if n == 6:
            return map1(lambda p: 0.0 if p == 0 else math.copysign(1.0, p))
        if n == 8:
            return map1(fsafe(math.floor))
        if n == 9:
            return map1(fsafe(math.ceil))
        if n == 10:
            return map1(fsafe(lambda p: p - math.floor(p)))
        if n == 13:
            return map1(fsafe(math.sin))
        if n == 14:
            return map1(fsafe(math.cos))
        if n == 25:     # Atan2(y, x)
            return map2(fsafe(math.atan2))
        if n == 26:
            return map2(fsafe(lambda p, q: p ** q))
        if n == 27:
            return map1(fsafe(math.exp))
        if n == 31:
            return map1(fsafe(math.sqrt))
        if n in (37, 79):
            return map2(nmin)
        if n in (40, 80):
            return map2(nmax)
        if n == 38:
            return map2(min)
        if n == 41:
            return map2(max)
        if n == 39:
            return map2(lambda p, q: p if signed(p) < signed(q) else q)
        if n == 42:
            return map2(lambda p, q: p if signed(p) > signed(q) else q)
        if n in (43, 81):
            return map3(lambda p, lo, hi: nmin(nmax(p, lo), hi))
        if n == 44:
            return map3(lambda p, lo, hi: min(max(p, lo), hi))
        if n == 46:
            return map3(lambda p, q, a_: f32(p * (1.0 - a_) + q * a_))
        if n == 48:
            return map2(lambda e, p: 0.0 if p < e else 1.0)
        if n == 49:
            return map3(fsafe(lambda e0, e1, p: (lambda s: s * s * (3 - 2 * s))(max(0.0, min(1.0, (p - e0) / (e1 - e0))))))
        if n == 50:
            return map3(lambda p, q, r: f32(p * q + r))
        if n == 58:     # PackHalf2x16
            return half_bits(x[0][0]) | half_bits(x[0][1]) << 16
        if n == 62:     # UnpackHalf2x16
            lo, hi = x[0] & 0xFFFF, x[0] >> 16
            return (struct.unpack("<e", struct.pack("<H", lo))[0], struct.unpack("<e", struct.pack("<H", hi))[0])
        if n == 66:
            return f32(math.sqrt(sum(p * p for p in x[0])))
        if n == 68:
            a_, b = x
            return (f32(a_[1] * b[2] - a_[2] * b[1]), f32(a_[2] * b[0] - a_[0] * b[2]), f32(a_[0] * b[1] - a_[1] * b[0]))
        if n == 69:
            ln = math.sqrt(sum(p * p for p in x[0]))
            return tuple(f32(p / ln) if ln else math.nan for p in x[0])
        raise Unsupported(f"GLSL.std.450 {n}")
