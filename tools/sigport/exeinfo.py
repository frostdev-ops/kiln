#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Per-exe function features for sigport (cached per exe sha256).

Functions come from .pdata (chained unwind entries folded into their primary function), plus leaf functions found as
direct call targets outside every .pdata range. For each function: size, instruction count, an exact normalised body
hash (rip-relative displacements and external branch targets masked, everything else kept: the idea of ShroudForge's
reciprocal-unique whole-procedure match, ShroudForge modules/game-profile-discovery, MIT), the mnemonic sequence (n-grams), call
targets in order, imported calls, referenced strings and .rdata values, immediates, struct displacements and rough CFG
shape (conditional branches, returns, internal branch targets).

    python3 tools/sigport/exeinfo.py <exe> [--cache DIR]       build/refresh the cache and print a summary
"""
import bisect
from array import array
import hashlib
import os
import pickle
import re
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from peimg import Image  # noqa: E402

VERSION = 3
DEFAULT_CACHE = Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache")) / "kiln" / "sigport"

RIP = re.compile(r"\[rip ([+-]) 0x([0-9a-f]+)\]")
HEX = re.compile(r"0x[0-9a-f]+")
MEM = re.compile(r"\[([^\]]+)\]")
DISP = re.compile(r"[+-] 0x([0-9a-f]+)\]")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _md():
    import capstone
    return capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)


def parse_imports(img, header):
    """IAT slot rva -> 'dll!name'. header: the file's first bytes (the mapped image has no headers)."""
    raw = img.img
    pe = struct.unpack_from("<I", header, 0x3C)[0]
    opt = pe + 24
    if struct.unpack_from("<H", header, opt)[0] != 0x20B:
        return {}
    imp_va, _imp_sz = struct.unpack_from("<II", header, opt + 112 + 8)
    out = {}
    if not imp_va:
        return out
    off = imp_va
    while off + 20 <= len(raw):
        oft, _ts, _fw, name, ft = struct.unpack_from("<IIIII", raw, off)
        if not (oft or name or ft):
            break
        try:
            dll = raw[name:raw.index(b"\0", name)].decode(errors="replace").lower()
        except ValueError:
            break
        src = oft or ft
        i = 0
        while True:
            if src + i * 8 + 8 > len(raw):
                break
            t = struct.unpack_from("<Q", raw, src + i * 8)[0]
            if t == 0:
                break
            if t & (1 << 63):
                sym = f"#{t & 0xFFFF}"
            elif t < len(raw):
                sym = raw[t + 2:raw.index(b"\0", t + 2)].decode(errors="replace")
            else:
                sym = "?"
            out[ft + i * 8] = f"{dll}!{sym}"
            i += 1
        off += 20
    return out


class Exe:
    """PE image + function table + lazily built features."""

    def __init__(self, path, cache=DEFAULT_CACHE, quiet=False):
        self.path = str(path)
        self.sha = sha256(path)
        self.image = Image(path)
        self.img = self.image.img
        self.base = self.image.base
        self.text_lo = self.image.text_va
        self.text_hi = self.image.text_va + len(self.image.text)
        self.sections = self.image.sections
        self.quiet = quiet
        self.cache = Path(cache) if cache else None
        self._load_or_build()

    # ---- sections -------------------------------------------------------------------------------------------------
    def section_of(self, rva):
        for name, (va, size) in self.sections.items():
            if va <= rva < va + size:
                return name
        return None

    def is_code(self, rva):
        return self.text_lo <= rva < self.text_hi

    # ---- functions ------------------------------------------------------------------------------------------------
    def _pdata(self):
        va, size = self.sections[".pdata"]
        rows = []
        for off in range(va, va + size, 12):
            b, e, u = struct.unpack_from("<III", self.img, off)
            if b == 0:
                break
            rows.append((b, e, u))
        by_begin = {r[0]: r for r in rows}

        def parent(u):
            if u & 1:  # indirect form: the unwind data field names the parent RUNTIME_FUNCTION
                return struct.unpack_from("<III", self.img, u & ~1)
            if u + 4 > len(self.img):
                return None
            flags = self.img[u] >> 3
            if flags & 4:
                cnt = self.img[u + 2]
                o = u + 4 + ((cnt + 1) & ~1) * 2
                return struct.unpack_from("<III", self.img, o)
            return None

        funcs = {}
        for b, e, u in rows:
            root = (b, e, u)
            for _ in range(32):
                p = parent(root[2])
                if p is None:
                    break
                root = by_begin.get(p[0], p)
            funcs.setdefault(root[0], []).append((b, e))
        return {k: sorted(v) for k, v in funcs.items()}

    def _leaf_extent(self, md, start, cap=0x1000):
        """Linear decode of a function without unwind data: stops at ret / jmp / int3 past every internal target."""
        code = self.img[start:min(start + cap, self.text_hi)]
        reach = start
        end = start
        for a, s, m, o in md.disasm_lite(code, start):
            end = a + s
            if m[0] == "j" and o.startswith("0x"):
                t = int(o, 16)
                if a < t < start + cap:
                    reach = max(reach, t)
            if (m in ("ret", "int3") or m == "jmp") and end > reach:
                break
        return end

    def _load_or_build(self):
        f = self.cache / f"{self.sha}.v{VERSION}.pkl" if self.cache else None
        if f and f.exists():
            with open(f, "rb") as fh:
                d = pickle.load(fh)
            self.__dict__.update(d)
            self._index()
            return
        self._build()
        if f:
            f.parent.mkdir(parents=True, exist_ok=True)
            tmp = f.with_suffix(f".tmp{os.getpid()}")
            with open(tmp, "wb") as fh:
                pickle.dump({k: getattr(self, k) for k in self._cached}, fh, protocol=5)
            tmp.replace(f)

    _cached = ("funcs", "imports", "mnem_names", "feat")

    def _index(self):
        self.starts = sorted(self.funcs)
        self.range_index = sorted((b, e, s) for s, rs in self.funcs.items() for b, e in rs)
        self.range_starts = [r[0] for r in self.range_index]
        self.mnem_id = {m: i for i, m in enumerate(self.mnem_names)}
        callers = {}
        for s, f in self.feat.items():
            for t in f["callees"]:
                callers.setdefault(t, []).append(s)
        self.callers = callers

    def _build(self):
        import time
        t0 = time.time()
        with open(self.path, "rb") as fh:
            header = fh.read(0x1000)
        self.imports = parse_imports(self.image, header)
        funcs = self._pdata()
        md = _md()
        # leaf functions: direct call targets outside every pdata range
        rng = sorted((b, e) for rs in funcs.values() for b, e in rs)
        rs = [r[0] for r in rng]

        def covered(t):
            i = bisect.bisect_right(rs, t) - 1
            return i >= 0 and rng[i][0] <= t < rng[i][1]

        self.funcs = funcs
        self.mnem_names = []
        mid = {}
        feats = {}
        pending = sorted(funcs)
        seen = set(funcs)
        k = 0
        while k < len(pending):
            s = pending[k]
            k += 1
            if s not in self.funcs:
                end = self._leaf_extent(md, s)
                if end <= s:
                    continue
                self.funcs[s] = [(s, end)]
            f = self._features(md, s, mid)
            feats[s] = f
            for t in f["callees"]:
                if t not in seen and self.is_code(t) and not covered(t):
                    seen.add(t)
                    pending.append(t)
        self.mnem_names = [None] * len(mid)
        for m, i in mid.items():
            self.mnem_names[i] = m
        self.feat = feats
        self._index()
        if not self.quiet:
            print(f"sigport: analysed {Path(self.path).name} ({self.sha[:8]}): {len(self.funcs)} functions "
                  f"in {time.time() - t0:.1f} s", file=sys.stderr)

    def _string_at(self, t):
        raw = self.img
        if not (0 <= t < len(raw)):
            return None
        e = raw.find(b"\0", t, t + 260)
        if e - t >= 4:
            s = raw[t:e]
            if all(32 <= c < 127 or c in (9, 10, 13) for c in s):
                return s.decode()
        # UTF-16LE
        out = []
        i = t
        while i + 1 < len(raw) and len(out) < 200:
            c = raw[i] | raw[i + 1] << 8
            if c == 0:
                break
            if not (32 <= c < 127 or c in (9, 10, 13)):
                return None
            out.append(chr(c))
            i += 2
        if len(out) >= 4:
            return "u:" + "".join(out)
        return None

    def _features(self, md, start, mid):
        rngs = self.funcs[start]
        mn = []
        body = []
        callees, imports, strings, rvals, data, imms, disps = [], [], [], [], [], [], []
        jcc = rets = 0
        targets = set()
        ninsn = 0
        size = 0
        lo = min(b for b, _ in rngs)
        hi = max(e for _, e in rngs)
        for b, e in rngs:
            size += e - b
            for a, s, m, o in md.disasm_lite(self.img[b:e], b):
                ninsn += 1
                i = mid.get(m)
                if i is None:
                    i = mid[m] = len(mid)
                mn.append(i)
                ops = o
                if m == "call" or m[0] == "j":
                    if o.startswith("0x"):
                        t = int(o, 16)
                        if lo <= t < hi:
                            ops = f"L{t - start:x}"
                            targets.add(t)
                        else:
                            ops = "T"
                            if m == "call" or m == "jmp":
                                callees.append(t)
                    if m[0] == "j" and m != "jmp":
                        jcc += 1
                elif m == "ret":
                    rets += 1
                if "rip" in o:
                    r = RIP.search(o)
                    if r:
                        t = a + s + (int(r.group(2), 16) * (1 if r.group(1) == "+" else -1))
                        ops = RIP.sub("[rip+X]", ops)
                        imp = self.imports.get(t)
                        if imp:
                            imports.append(imp)
                            ops = ops.replace("[rip+X]", f"[imp:{imp}]")
                        elif self.is_code(t):
                            callees.append(t)  # function pointer (lea rcx, [rip+fn])
                        else:
                            st = self._string_at(t) if m == "lea" else None
                            if st is not None:
                                strings.append(st)
                            else:
                                sec = self.section_of(t)
                                if sec == ".rdata" and m != "lea":
                                    rvals.append(bytes(self.img[t:t + 8]))
                                else:
                                    data.append(t)
                elif "0x" in o:
                    if "[" in o:
                        d = DISP.search(o)
                        if d:
                            v = int(d.group(1), 16)
                            if v >= 0x100:
                                disps.append(v)
                        head = o[:o.index("[")] + o[o.index("]") + 1:]
                        for h in HEX.findall(head):
                            v = int(h, 16)
                            if v >= 0x100:
                                imms.append(v)
                    else:
                        for h in HEX.findall(o):
                            v = int(h, 16)
                            if v >= 0x100:
                                imms.append(v)
                body.append(f"{m} {ops}")
        h = hashlib.blake2b("\n".join(body).encode(), digest_size=12).digest()
        return {"size": size, "ninsn": ninsn, "hash": h, "mn": array("H", mn).tobytes(),
                "callees": callees, "imports": imports, "strings": strings, "rvals": rvals, "data": data,
                "imms": imms, "disps": disps, "jcc": jcc, "rets": rets, "blocks": len(targets) + 1}

    # ---- queries --------------------------------------------------------------------------------------------------
    def func_of(self, rva):
        """Start of the function whose ranges contain rva, else None."""
        i = bisect.bisect_right(self.range_starts, rva) - 1
        if i >= 0 and self.range_index[i][0] <= rva < self.range_index[i][1]:
            return self.range_index[i][2]
        return None

    def ranges(self, start):
        return self.funcs.get(start, [])

    def add_pseudo(self, start, end):
        """Register a function the table does not know (a leaf reached only by a tail jump or a pointer)."""
        if start in self.funcs:
            return start
        md = _md()
        self.funcs[start] = [(start, end)]
        mid = dict(self.mnem_id)
        self.feat[start] = self._features(md, start, mid)
        if len(mid) != len(self.mnem_id):
            for m, i in mid.items():
                if i >= len(self.mnem_names):
                    self.mnem_names.append(m)
            self.mnem_id = mid
        self.starts = sorted(self.funcs)
        self.range_index = sorted((b, e, s) for s, rs in self.funcs.items() for b, e in rs)
        self.range_starts = [r[0] for r in self.range_index]
        for t in self.feat[start]["callees"]:
            self.callers.setdefault(t, []).append(start)
        return start

    def pseudo_function(self, rva):
        """Bounds of the code around rva when no function contains it: back to padding/previous end, forward to ret."""
        lo = rva
        floor = max(self.text_lo, rva - 0x800)
        while lo > floor and self.img[lo - 1] != 0xCC:
            prev = self.func_of(lo - 1)
            if prev is not None:
                break
            lo -= 1
        md = _md()
        end = self._leaf_extent(md, lo, cap=0x1000)
        if end <= rva:
            end = self._leaf_extent(md, rva, cap=0x1000)
        return lo, max(end, rva + 1)

    def disasm(self, start, end):
        """Detailed instructions [start, end): list of dicts (address, size, bytes, mnemonic, op_str, disp/imm offsets)."""
        import capstone
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        md.detail = True
        out = []
        for i in md.disasm(bytes(self.img[start:end]), start):
            # capstone 5 reports disp_size 2 under a 0x66 prefix; the displacement runs up to the immediate (or the end)
            disp_size = ((i.imm_offset if i.imm_offset else i.size) - i.disp_offset) if i.disp_offset else 0
            d = {"a": i.address, "s": i.size, "b": bytes(i.bytes), "m": i.mnemonic, "o": i.op_str,
                 "disp_off": i.disp_offset, "disp_size": disp_size, "imm_off": i.imm_offset, "imm_size": i.imm_size,
                 "rip": "rip" in i.op_str}
            d["branch"] = (i.mnemonic == "call" or i.mnemonic.startswith("j")) and i.op_str.startswith("0x")
            out.append(d)
        return out

    def function_insns(self, start):
        out = []
        for b, e in self.ranges(start):
            out.extend(self.disasm(b, e))
        return out


def norm_token(ins, keep_regs=True):
    """Instruction token for alignment: numbers masked, registers kept (keep_regs) or masked too."""
    o = ins["o"]
    if ins["branch"]:
        o = "T"
    else:
        o = RIP.sub("[rip+X]", o)
        o = HEX.sub("K", o)
        o = re.sub(r"\b\d+\b", "K", o)
    if not keep_regs:
        o = re.sub(r"\b[re]?[a-z]{1,2}[0-9]{0,2}[dwb]?\b", "R", o)
    return f"{ins['m']} {o}"


def main():
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("exe")
    ap.add_argument("--cache", default=str(DEFAULT_CACHE))
    a = ap.parse_args()
    e = Exe(a.exe, a.cache)
    n_leaf = sum(1 for s, r in e.funcs.items() if len(r) == 1 and s not in e.feat)
    hashes = {}
    for s, f in e.feat.items():
        hashes.setdefault(f["hash"], []).append(s)
    uniq = sum(1 for v in hashes.values() if len(v) == 1)
    print(f"{e.path}: sha256 {e.sha}, {len(e.funcs)} functions ({n_leaf} without features), "
          f"{len(e.imports)} imports, {uniq} unique bodies")
    return 0


if __name__ == "__main__":
    sys.exit(main())
