#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""sigport port: carry the symbol database from one game exe to another.

Given an old exe on which the definitions resolve and a new exe (a game update, or the other side of the same build),
find every pattern site in the new exe, re-derive typed holes, regenerate shortest unique patterns (keeping the holes)
and write a candidate build record. Never edits the live sigdb: the output folder holds a complete patched tree, a
unified diff against the input tree, a report with a confidence per symbol and constant, and an unverified
build record.

    python3 tools/sigport/port.py --old OLD.exe --new NEW.exe [--defs sigdb/symbols/server.json] [--out DIR]
        [--no-exact] [--no-generalize] [--regenerate-all] [--min-confidence 0.8] [--steam-build N] [--svn N]

Site location, per strategy (pattern) of every symbol, in this order:
  exact      the old pattern matches once in the new search range, inside the image of the old site's function
  mapped     the old site's function is matched (matcher.py); the site instruction is found by aligning the two
             functions' instruction streams
  relaxed    the old pattern with every displacement/immediate wildcarded matches once inside the matched function
             (or the neighbourhood window between matched functions)
Holes are re-derived by locating the same instruction in the new site and reading the same operand (displacement,
immediate or branch target). Layout constants come out of a resolve of the patched definitions on the new exe; each
is reported with its captures (votes) and with corroborating sites: other matched functions that use the old value in
the same operand, read through the instruction alignment.
Exit status: 0 every symbol and constant that resolved on the old exe is ported with confidence >= --min-confidence;
1 some are missing or below it (see the report); 2 bad input.
"""
import argparse
import bisect
import copy
import datetime
import difflib
import json
import shutil
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_record import codeview  # noqa: E402
from defs import Editor, Tree, new_overlay  # noqa: E402
from exeinfo import DEFAULT_CACHE, Exe, norm_token  # noqa: E402
from matcher import Matcher, _shape, align  # noqa: E402
from sigdb_check import WIDTH, Resolver, parse  # noqa: E402

REPO = Path(__file__).resolve().parents[2]
TOOL_VERSION = 1
PAIR_CONF = {"identical": 1.0, "callsite": 0.98}
MAX_INSNS = 40
# literal (non-wildcard) bytes a regenerated pattern keeps at least: function prologues recur, so whole-image patterns
# get more than patterns scoped to a function
MIN_LITERALS = {"scoped": 6, "global": 12}


def scan(img, rx, lo, hi, limit=2):
    hits, i = [], lo
    while len(hits) < limit:
        m = rx.search(img, i, hi)
        if not m:
            break
        hits.append(m.start())
        i = m.start() + 1
    return hits


def unique_at(exe, pattern, lo, hi, at):
    rx = parse(pattern)[0]
    return scan(exe.img, rx, lo, hi) == [at]


def role_of(ins, k, width):
    """(role, offset inside the field) of the hole bytes [k, k+width) of an instruction, or None."""
    if ins["branch"] and ins["imm_size"] and k == ins["imm_off"]:
        return "rel", 0
    for role, off, size in (("disp", ins["disp_off"], ins["disp_size"]), ("imm", ins["imm_off"], ins["imm_size"])):
        if off and size and off <= k and k + width <= off + size:
            return role, k - off
    return None


def field(ins, role):
    if role == "disp":
        return ins["disp_off"], ins["disp_size"]
    return ins["imm_off"], ins["imm_size"]


def read_value(img, at, width, kind):
    raw = bytes(img[at:at + width])
    if kind == "s32":
        return struct.unpack("<i", raw)[0]
    return int.from_bytes(raw, "little")


def tokens_text(toks):
    return " ".join(toks)


class Strat:
    def __init__(self, name, idx, f):
        self.name, self.idx, self.f = name, idx, f
        self.pattern = f["pattern"]
        self.rx, self.holes, self.length = parse(self.pattern)
        self.offset = f.get("offset", 0)
        self.scope = f.get("scope")
        self.probe = name.startswith("@")
        self.tag = f"{name}.find[{idx}]"
        self.old_hit = None
        self.old_n = 0
        self.old_range = None
        self.old_values = {}
        self.site = None
        self.method = None
        self.conf = 0.0
        self.notes = []
        self.holes_new = []
        self.new_pattern = None
        self.new_offset = None
        self.new_size = None
        self.generalized = False
        self.values = {}
        self.error = None
        self.fo = self.fn = None

    def row(self):
        return {"tag": self.tag, "old_hit": hex(self.old_hit) if self.old_hit is not None else None,
                "new_hit": hex(self.site) if self.site is not None else None, "method": self.method,
                "confidence": round(self.conf, 3), "old_pattern": self.pattern,
                "new_pattern": self.new_pattern, "pattern_changed": self.new_pattern not in (None, self.pattern),
                "generalized": self.generalized, "offset": self.new_offset, "scope_size": self.new_size,
                "values": {k: hex(v) if isinstance(v, int) else v for k, v in self.values.items()},
                "old_values": {k: hex(v) for k, v in self.old_values.items()},
                "notes": self.notes, "error": self.error,
                "old_function": hex(self.fo) if self.fo is not None else None,
                "new_function": hex(self.fn) if self.fn is not None else None}


class Porter:
    def __init__(self, old, new, tree, opts, log):
        self.a, self.b, self.tree, self.o, self.log = old, new, tree, opts, log
        self.doc = tree.doc
        self._fi = ({}, {})
        self._al = {}
        self.new_rva = {}
        self.own_rva = set()
        self.rva_votes = {}

    # ---- helpers --------------------------------------------------------------------------------------------------
    def finsns(self, side, start):
        c = self._fi[side]
        if start not in c:
            e = self.a if side == 0 else self.b
            c[start] = e.function_insns(start)
        return c[start]

    def stream(self, side, fstart, addr, n=0x200):
        """Instructions from the one containing addr (decoded with its function, so a pattern that starts after an
        instruction's prefix bytes still sees whole instructions), continued linearly past the function's end."""
        e = self.a if side == 0 else self.b
        if fstart is not None:
            L = self.finsns(side, fstart)
            k = self.containing(L, addr)
            if k is not None:
                res = [L[k]]
                for i in L[k + 1:]:
                    if i["a"] != res[-1]["a"] + res[-1]["s"]:
                        break
                    res.append(i)
                end = res[-1]["a"] + res[-1]["s"]
                if end < addr + n:
                    res += e.disasm(end, addr + n)
                return res
        return e.disasm(addr, addr + n)

    def falign(self, fo, fn):
        k = (fo, fn)
        if k not in self._al:
            lo, ln = self.finsns(0, fo), self.finsns(1, fn)
            to = [norm_token(i) for i in lo]
            tn = [norm_token(i) for i in ln]
            self._al[k] = align(to, tn)
        return self._al[k]

    def pair_conf(self, fo):
        how, score = self.M.how.get(fo, ("window", 0.0))
        if how in PAIR_CONF:
            return PAIR_CONF[how]
        return min(0.95, 0.55 + 0.45 * score)

    def text_range(self, e):
        return e.text_lo, e.text_hi

    def old_range(self, s):
        if s.scope:
            r = self.old_syms.get(s.scope["symbol"], {})
            if not r.get("ok"):
                return None
            return r["rva"], min(self.a.text_hi, r["rva"] + s.scope["size"])
        return self.text_range(self.a)

    def new_range(self, s, size=None):
        if s.scope:
            base = self.new_rva.get(s.scope["symbol"])
            if base is None:
                return None
            return base, min(self.b.text_hi, base + (size or s.scope["size"]))
        return self.text_range(self.b)

    # ---- old side -------------------------------------------------------------------------------------------------
    def analyse_old(self):
        self.old_syms, self.old_consts, self.old_feats = Resolver(self.a.image, copy.deepcopy(self.doc)).run()
        self.strats = []
        for name, d in self.doc["symbols"].items():
            for i, f in enumerate(d["find"]):
                s = Strat(name, i, f)
                self.strats.append(s)
                rng = self.old_range(s)
                if rng is None:
                    s.error = "scope symbol unresolved on the old exe"
                    continue
                s.old_range = rng
                hits = scan(self.a.img, s.rx, rng[0], rng[1])
                s.old_n = len(hits)
                if len(hits) != 1:
                    s.error = f"{len(hits)}{'+' if len(hits) > 1 else ''} matches on the old exe"
                    continue
                s.old_hit = hits[0]
                for hname, kind, off in s.holes:
                    v = read_value(self.a.img, s.old_hit + off, WIDTH[kind], kind)
                    if kind == "rel32":
                        v = s.old_hit + off + 4 + struct.unpack("<i", struct.pack("<I", v))[0]
                    s.old_values[hname] = v

    # ---- site location --------------------------------------------------------------------------------------------
    def site_function(self, exe, x):
        f = exe.func_of(x)
        if f is not None:
            return f, False
        lo, hi = exe.pseudo_function(x)
        return exe.add_pseudo(lo, hi), True

    def window(self, fo):
        """b-rva range between the images of fo's nearest matched neighbours."""
        ms = self._matched_sorted
        k = bisect.bisect_left(ms, fo)
        lo = self.M.m[ms[k - 1]] if k > 0 else self.b.text_lo
        k2 = bisect.bisect_right(ms, fo)
        hi = self.M.m[ms[k2]] if k2 < len(ms) else self.b.text_hi
        if hi <= lo:
            return None
        if hi - lo > 0x40000:
            return None
        return lo, hi + 0x400

    def map_fn(self, s, fo, pseudo):
        fn = self.M.m.get(fo)
        if fn is not None:
            return fn, self.pair_conf(fo), self.M.how[fo][0]
        if pseudo:
            return None, 0.0, None
        c = self.M.best_for(fo, limit=80)
        if c and c[0][0] >= 0.45 and (len(c) == 1 or c[0][0] - c[1][0] >= 0.05):
            s.notes.append(f"function matched on demand (window, similarity {c[0][0]:.2f})")
            return c[0][1], min(0.9, 0.5 + 0.45 * c[0][0]), "window"
        return None, 0.0, None

    def relaxed_pattern(self, s):
        span = [i for i in self.stream(0, s.fo, s.old_hit, s.length + 15) if i["a"] < s.old_hit + s.length]
        toks = []
        hole_at = {s.old_hit + off: WIDTH[kind] for _, kind, off in s.holes}
        end = s.old_hit + s.length
        for ins in span:
            wild = set()
            for off, size in ((ins["disp_off"], ins["disp_size"]), (ins["imm_off"], ins["imm_size"])):
                if off and size:
                    wild |= set(range(off, off + size))
            p = 0
            while p < ins["s"] and ins["a"] + p < end:
                a = ins["a"] + p
                if a < s.old_hit:
                    p += 1
                    continue
                if a in hole_at:
                    toks += ["??"] * hole_at[a]
                    p += hole_at[a]
                    continue
                toks.append("??" if p in wild else f"{ins['b'][p]:02X}")
                p += 1
        return " ".join(toks)

    @staticmethod
    def containing(insns, addr):
        """Index of the instruction of insns (sorted) that contains addr, else None."""
        lo, hi = 0, len(insns)
        while lo < hi:
            mid = (lo + hi) // 2
            if insns[mid]["a"] <= addr:
                lo = mid + 1
            else:
                hi = mid
        k = lo - 1
        if k >= 0 and insns[k]["a"] <= addr < insns[k]["a"] + insns[k]["s"]:
            return k
        return None

    @staticmethod
    def prefix_len(ins):
        n = 0
        for c in ins["b"]:
            if c in (0x66, 0x67, 0xF2, 0xF3, 0x2E, 0x3E, 0x26, 0x64, 0x65, 0x36, 0xF0) or 0x40 <= c <= 0x4F:
                n += 1
                if 0x40 <= c <= 0x4F:
                    break
            else:
                break
        return n

    def shape_search(self, s, lo, ln, mp, k):
        """The pattern's own instructions (registers and numbers masked) as a contiguous run in the new function,
        nearest to where the aligned neighbours put it. Survives instruction scheduling and register allocation
        changes that defeat the whole-function alignment."""
        k1 = self.containing(lo, s.old_hit + s.length - 1)
        if k1 is None:
            return None
        want = [_shape(norm_token(i)) for i in lo[k:k1 + 1]]
        if len(want) < 2:
            return None
        shapes = [_shape(norm_token(i)) for i in ln]
        hits = [p for p in range(len(ln) - len(want) + 1) if shapes[p:p + len(want)] == want]
        if not hits:
            return None
        # expected position from the nearest aligned instruction before the site
        exp = None
        for kk in range(k, -1, -1):
            if kk in mp:
                exp = mp[kk][0] + (k - kk)
                break
        if len(hits) > 1:
            if exp is None:
                return None
            hits.sort(key=lambda p: abs(p - exp))
            if abs(hits[0] - exp) == abs(hits[1] - exp):
                return None
        p = hits[0]
        same = all(norm_token(lo[k + i]) == norm_token(ln[p + i]) for i in range(len(want)))
        s.span_map = {k + i: (p + i, 0.95 if same else 0.88) for i in range(len(want))}
        s.notes.append(f"site found by instruction-shape search ({len(hits)} candidate run(s); "
                       f"{'same registers' if same else 'registers differ'})")
        delta = s.old_hit - lo[k]["a"]
        po, pn = self.prefix_len(lo[k]), self.prefix_len(ln[p])
        if delta and delta <= po:
            delta = pn - (po - delta)
        return ln[p]["a"] + delta

    def locate(self, s):
        a, b = self.a, self.b
        x_old = s.old_hit + (0 if s.probe else s.offset)
        fo, pseudo = self.site_function(a, x_old)
        s.fo = fo
        rng = self.new_range(s)
        if rng is None:
            s.error = "scope symbol not ported"
            return False
        fn, pconf, how = self.map_fn(s, fo, pseudo)
        exact_alt = None
        if not self.o.no_exact:
            hits = scan(b.img, s.rx, rng[0], rng[1])
            if len(hits) == 1:
                xn = hits[0] + (0 if s.probe else s.offset)
                inside = fn is not None and any(lo <= xn < hi for lo, hi in b.ranges(fn))
                if fn is None or inside:
                    s.site, s.method = hits[0], "exact"
                    s.conf = 0.99 if inside else 0.95
                    s.fn = fn if inside else b.func_of(xn)
                    return True
                exact_alt = hits[0]
                s.notes.append(f"old pattern matches once at {hits[0]:#x}, outside the matched function {fn:#x}")
        if fn is not None and not pseudo:
            lo, ln = self.finsns(0, fo), self.finsns(1, fn)
            mp = self.falign(fo, fn)
            k = self.containing(lo, s.old_hit)
            site = None
            if k is not None and k in mp:
                # the pattern may start after an instruction's prefixes (hand-written patterns skip REX bytes)
                j, q = mp[k]
                delta = s.old_hit - lo[k]["a"]
                po, pn = self.prefix_len(lo[k]), self.prefix_len(ln[j])
                if delta and delta <= po:
                    delta = pn - (po - delta)
                site = ln[j]["a"] + delta
            elif not s.probe and s.offset:
                # pattern starts before the function (int3 padding): anchor on the symbol, keep the prefix bytes
                kx = self.containing(lo, x_old)
                if kx is not None and kx in mp and lo[kx]["a"] == x_old:
                    j, q = mp[kx]
                    xn = ln[j]["a"]
                    if bytes(b.img[xn - s.offset:xn]) == bytes(a.img[s.old_hit:x_old]):
                        site = xn - s.offset
                    else:
                        s.notes.append("bytes before the function changed")
            if site is None and k is not None:
                site = self.shape_search(s, lo, ln, mp, k)
            if site is not None:
                s.site, s.method, s.fn = site, "mapped", fn
                s.conf = pconf
                if how not in ("identical", "callsite"):
                    s.notes.append(f"function pair by {how} (similarity {self.M.how.get(fo, ('', 0))[1]})")
                return True
        # relaxed: operands wildcarded, inside the matched function or the neighbourhood window
        if fn is not None:
            region = (min(lo for lo, _ in b.ranges(fn)), max(hi for _, hi in b.ranges(fn)) + s.length)
        else:
            region = self.window(fo)
        if region is not None and s.scope:
            region = (max(region[0], rng[0]), min(region[1], rng[1]))
        if region is None and s.scope:
            region = rng
        if region is not None and region[1] > region[0]:
            rp = self.relaxed_pattern(s)
            hits = scan(b.img, parse(rp)[0], region[0], region[1])
            if len(hits) == 1:
                s.site, s.method = hits[0], "relaxed"
                xn = hits[0] + (0 if s.probe else s.offset)
                if fn is None:
                    fn, _ = self.site_function(b, xn)
                    # no function match: judge the found code by its similarity to the old function
                    sim = 1.0 if a.feat[fo]["hash"] == b.feat[fn]["hash"] else self.M.sim(fo, fn)
                    pconf = 0.55 + 0.4 * sim
                    s.notes.append(f"found in the neighbourhood window; similarity to the old function {sim:.2f}")
                s.fn = fn
                s.conf = pconf * 0.95
                s.notes.append(f"relaxed pattern {rp}")
                return True
            s.notes.append(f"relaxed pattern: {len(hits)} matches in {region[0]:#x}..{region[1]:#x}")
        if exact_alt is not None:
            s.site, s.method, s.conf = exact_alt, "exact-unconfirmed", 0.5
            s.fn = b.func_of(exact_alt)
            return True
        s.error = "site not found" + (" (old function unmatched)" if fn is None else "")
        return False

    # ---- holes ----------------------------------------------------------------------------------------------------
    def derive(self, s):
        a, b = self.a, self.b
        out = []
        if s.method != "mapped":
            # exact / relaxed: the new bytes have the old pattern's layout, holes sit at the same offsets
            for hname, kind, off in s.holes:
                at = s.site + off
                v = read_value(b.img, at, WIDTH[kind], kind)
                if kind == "rel32":
                    v = at + 4 + struct.unpack("<i", struct.pack("<I", v))[0]
                out.append((hname, kind, at, WIDTH[kind], v))
                s.values[hname] = v
            s.new_offset = s.offset
        else:
            lo, ln = self.finsns(0, s.fo), self.finsns(1, s.fn)
            mp = dict(self.falign(s.fo, s.fn))
            mp.update(getattr(s, "span_map", {}))
            qmin = 1.0
            for hname, kind, off in s.holes:
                addr = s.old_hit + off
                j = self.containing(lo, addr)
                if j is None:
                    s.error = f"hole {hname}: outside the old function"
                    return False
                ins = lo[j]
                rs = role_of(ins, addr - ins["a"], WIDTH[kind])
                if rs is None:
                    s.error = f"hole {hname}: not an operand of `{ins['m']} {ins['o']}`"
                    return False
                role, sub = rs
                whole = sub == 0 and WIDTH[kind] == field(ins, "imm" if role == "rel" else role)[1]
                if j not in mp:
                    s.error = f"hole {hname}: instruction `{ins['m']} {ins['o']}` has no counterpart in the new function"
                    return False
                j2, q = mp[j]
                ins2 = ln[j2]
                qmin = min(qmin, q)
                if ins2["m"] != ins["m"]:
                    s.error = f"hole {hname}: `{ins['m']}` became `{ins2['m']}`"
                    return False
                if role == "rel" and not ins2["branch"]:
                    s.error = f"hole {hname}: the new instruction is not a direct branch"
                    return False
                k2, w2 = field(ins2, "imm" if role == "rel" else role)
                if not w2 or (role != "rel" and not k2):
                    s.error = f"hole {hname}: `{ins2['m']} {ins2['o']}` has no {role} operand"
                    return False
                if not whole:
                    if w2 != field(ins, role)[1]:
                        s.error = f"hole {hname}: part of a {field(ins, role)[1]}-byte operand that is now {w2} bytes"
                        return False
                    k2, w2 = k2 + sub, WIDTH[kind]
                nkind = kind
                if w2 != WIDTH[kind]:
                    if kind in ("d8", "d32") and w2 in (1, 4):
                        nkind = "d8" if w2 == 1 else "d32"
                        s.notes.append(f"hole {hname}: {kind} -> {nkind} (encoding changed)")
                        qmin = min(qmin, 0.9)
                    else:
                        s.error = f"hole {hname}: operand width {w2} does not fit {kind}"
                        return False
                if role == "rel" and k2 + w2 != ins2["s"]:
                    s.error = f"hole {hname}: branch displacement is not the instruction's last field"
                    return False
                at = ins2["a"] + k2
                if at < s.site:
                    s.error = f"hole {hname}: lands before the new pattern start"
                    return False
                v = read_value(b.img, at, w2, nkind)
                if nkind == "rel32":
                    v = at + 4 + struct.unpack("<i", struct.pack("<I", v))[0]
                out.append((hname, nkind, at, w2, v))
                s.values[hname] = v
            # the symbol inside the pattern (offset): its instruction's counterpart
            if s.new_offset is None:
                s.new_offset = s.offset
                if not s.probe and s.offset:
                    x_old = s.old_hit + s.offset
                    kx = self.containing(lo, x_old)
                    if kx is not None and kx in mp and lo[kx]["a"] == x_old:
                        s.new_offset = ln[mp[kx][0]]["a"] - s.site
                    elif kx is None and s.old_hit < min(r[0] for r in a.ranges(s.fo)):
                        pass  # prefix before the function: same offset
                    else:
                        s.error = "the symbol's instruction has no counterpart in the new function"
                        return False
                    if s.new_offset != s.offset:
                        s.notes.append(f"offset {s.offset} -> {s.new_offset}")
            # mapping quality of the pattern's own instructions
            k0 = self.containing(lo, s.old_hit)
            k1 = self.containing(lo, s.old_hit + s.length - 1)
            if k0 is not None and k1 is not None:
                qs = [mp[k][1] if k in mp else 0.5 for k in range(k0, k1 + 1)]
                qmin = min(qmin, sum(qs) / len(qs) + 0.1)
            s.conf *= min(1.0, qmin) if qmin < 0.99 else 0.98
        s.holes_new = out
        # rel32 captures: the matched callee must be the captured target
        for hname, nkind, _at, _w, v in out:
            if nkind != "rel32":
                continue
            exp = self.M.m.get(s.old_values.get(hname))
            if exp is None:
                continue
            if exp == v:
                s.notes.append(f"{hname}: target agrees with the function match")
                if s.method != "exact":
                    # an independent second witness for the site: halve the remaining doubt
                    s.conf = min(0.97, s.conf + (1 - s.conf) * 0.5)
            else:
                s.notes.append(f"{hname}: target {v:#x}, the function match says {exp:#x}")
                s.conf *= 0.6
        return True

    # ---- patterns -------------------------------------------------------------------------------------------------
    @staticmethod
    def emit(insns, holes, n, start):
        toks = []
        for ins in insns[:n]:
            wild = set()
            if ins["branch"] and ins["imm_size"]:
                wild |= set(range(ins["imm_off"], ins["imm_off"] + ins["imm_size"]))
            if ins["rip"] and ins["disp_size"]:
                wild |= set(range(ins["disp_off"], ins["disp_off"] + ins["disp_size"]))
            p = 0
            while p < ins["s"]:
                a = ins["a"] + p
                if a < start:
                    p += 1
                    continue
                if a in holes:
                    name, kind, w = holes[a]
                    toks.append((f"{{{name}:{kind}}}", w, True))
                    p += w
                    continue
                toks.append(("??" if p in wild else f"{ins['b'][p]:02X}", 1, False))
                p += 1
        return toks

    @staticmethod
    def emit_general(old, new, hold, hnew, n, ostart, nstart):
        toks = []
        for k in range(n):
            if k >= len(old) or k >= len(new):
                return None
            io, iN = old[k], new[k]
            if io["s"] != iN["s"] or io["m"] != iN["m"] or (io["a"] - ostart) != (iN["a"] - nstart):
                return None
            wild = set()
            for ins in (io, iN):
                if ins["branch"] and ins["imm_size"]:
                    wild |= set(range(ins["imm_off"], ins["imm_off"] + ins["imm_size"]))
                if ins["rip"] and ins["disp_size"]:
                    wild |= set(range(ins["disp_off"], ins["disp_off"] + ins["disp_size"]))
            # a displacement/immediate that differs is a moved layout value: wildcard the whole field
            for off, size in ((iN["disp_off"], iN["disp_size"]), (iN["imm_off"], iN["imm_size"])):
                if off and size and io["b"][off:off + size] != iN["b"][off:off + size]:
                    wild |= set(range(off, off + size))
            p = 0
            while p < iN["s"]:
                ao, an = io["a"] + p, iN["a"] + p
                if an < nstart:
                    p += 1
                    continue
                if ao in hold or an in hnew:
                    if not (ao in hold and an in hnew) or hold[ao][:2] != hnew[an][:2]:
                        return None
                    name, kind, w = hnew[an]
                    toks.append((f"{{{name}:{kind}}}", w, True))
                    p += w
                    continue
                if p in wild or io["b"][p] != iN["b"][p]:
                    toks.append(("??", 1, False))
                else:
                    toks.append((f"{iN['b'][p]:02X}", 1, False))
                p += 1
        return toks

    def shortest(self, make, checks, need_bytes, min_lit):
        """Shortest unique pattern: grow by instructions, then trim trailing bytes (uniqueness is monotone)."""
        for n in range(1, MAX_INSNS + 1):
            toks = make(n)
            if toks is None:
                return None
            nbytes = sum(w for _, w, _ in toks)
            if nbytes < need_bytes:
                continue
            while toks and toks[-1][0] == "??":
                toks.pop()
            if sum(1 for t, _, h in toks if t != "??" and not h) < min_lit:
                continue
            pat = tokens_text(t for t, _, _ in toks)
            if not all(unique_at(e, pat, lo, hi, at) for e, lo, hi, at in checks):
                continue
            # trim trailing bytes: keep need_bytes and every hole
            req, acc = len(toks), 0
            for idx, (t, w, h) in enumerate(toks):
                acc += w
                if acc >= need_bytes:
                    req = idx + 1
                    break
            req = max(req, max((i + 1 for i, (_, _, h) in enumerate(toks) if h), default=0))
            best = len(toks)
            l, r = req, len(toks)
            while l < r:
                mid = (l + r) // 2
                cand = toks[:mid]
                while cand and cand[-1][0] == "??":
                    cand = cand[:-1]
                ok = (sum(1 for t, _, h in cand if t != "??" and not h) >= min_lit and
                      all(unique_at(e, tokens_text(t for t, _, _ in cand), lo, hi, at) for e, lo, hi, at in checks))
                if ok:
                    best, r = mid, mid
                else:
                    l = mid + 1
            cand = toks[:best]
            while cand and cand[-1][0] == "??":
                cand = cand[:-1]
            return tokens_text(t for t, _, _ in cand)
        return None

    def regenerate(self, s):
        a, b = self.a, self.b
        if s.new_offset is None:
            s.new_offset = s.offset
        hnew = {at: (name, kind, w) for name, kind, at, w, _v in s.holes_new}
        hold = {s.old_hit + off: (name, kind, WIDTH[kind]) for name, kind, off in s.holes}
        kinds_same = all(k == ok for (_, k, _, _, _), (_, ok, _) in zip(s.holes_new, s.holes))
        anchor = s.site + (0 if s.probe else s.new_offset)
        need = max([anchor + 1] + [at + w for at, (_, _, w) in hnew.items()]) - s.site
        sizes = [None]
        if s.scope:
            base = self.new_rva[s.scope["symbol"]]
            orig = s.scope["size"]
            sizes = [orig]
            for extra, step in ((0x80, 0x40), (0x200, 0x100)):
                z = -(-(s.site - base + max(need, s.length) + extra) // step) * step
                if z > sizes[-1]:
                    sizes.append(z)
        new_insns = self.stream(1, s.fn, s.site)
        old_insns = self.stream(0, s.fo, s.old_hit)
        min_lit = MIN_LITERALS["scoped" if s.scope else "global"]
        for size in sizes:
            rng = self.new_range(s, size)
            if size is None or size == s.scope["size"]:
                # keep the old pattern when it still resolves here with the same hole kinds
                if (not self.o.regenerate_all and s.method == "exact" and kinds_same and s.new_offset == s.offset
                        and unique_at(b, s.pattern, rng[0], rng[1], s.site)):
                    s.new_pattern = s.pattern
                    return True
            checks_new = [(b, rng[0], rng[1], s.site)]
            pat = None
            if not self.o.no_generalize and s.new_offset == s.offset:
                orng = s.old_range if size is None else (s.old_range[0], min(a.text_hi, s.old_range[0] + size))
                pat = self.shortest(lambda n: self.emit_general(old_insns, new_insns, hold, hnew, n, s.old_hit, s.site),
                                    checks_new + [(a, orng[0], orng[1], s.old_hit)], need, min_lit)
                s.generalized = pat is not None
            if pat is None:
                pat = self.shortest(lambda n: self.emit(new_insns, hnew, n, s.site), checks_new, need, min_lit)
            if pat is None:
                continue
            s.new_pattern = pat
            if size is not None and size != s.scope["size"]:
                s.new_size = size
                s.notes.append(f"scope size {s.scope['size']} -> {size}")
            if not s.generalized and not self.o.no_generalize:
                s.notes.append("pattern is specific to the new exe (no common pattern resolves on both)")
            return True
        s.error = f"no unique pattern within {MAX_INSNS} instructions"
        return False

    # ---- driver ---------------------------------------------------------------------------------------------------
    def run(self):
        t0 = time.time()
        self.analyse_old()
        self.log(f"old exe: {sum(1 for s in self.strats if s.old_hit is not None)}/{len(self.strats)} strategies "
                 f"match once ({time.time() - t0:.1f} s)")
        self.M = Matcher(self.a, self.b, log=self.log).run()
        self._matched_sorted = sorted(self.M.m)
        pending = [s for s in self.strats if s.old_hit is not None]
        while pending:
            rest = [s for s in pending if s.scope and s.scope["symbol"] not in self.new_rva]
            ready = [s for s in pending if s not in rest]
            if not ready:
                break
            for s in ready:
                self.port_one(s)
            pending = rest
        for s in pending:
            if s.error is None:
                s.error = f"scope symbol {s.scope['symbol']} not ported"
        self.log(f"ported {sum(1 for s in self.strats if s.new_pattern)} strategies ({time.time() - t0:.1f} s)")
        return self

    def port_one(self, s):
        if not self.locate(s):
            return
        if not self.derive(s):
            return
        if not self.regenerate(s):
            return
        if not s.probe:
            rva = s.site + s.new_offset
            self.rva_votes.setdefault(s.name, []).append((rva, s.tag, s.conf))
            if s.name not in self.own_rva:  # the symbol's own first pattern wins over captures, like the resolver
                self.own_rva.add(s.name)
                self.new_rva[s.name] = rva
        for hname, kind, _at, _w, v in s.holes_new:
            if kind == "rel32":
                self.rva_votes.setdefault(hname, []).append((v, s.tag, s.conf))
                if hname not in self.own_rva:
                    self.new_rva.setdefault(hname, v)


# ---- outputs ------------------------------------------------------------------------------------------------------
def patched_doc(doc, strats, min_conf):
    d = copy.deepcopy(doc)
    for s in strats:
        if s.new_pattern is None or s.conf < min_conf:
            continue
        f = d["symbols"][s.name]["find"][s.idx]
        f["pattern"] = s.new_pattern
        if s.new_offset:
            f["offset"] = s.new_offset
        elif "offset" in f and s.new_offset == 0:
            f.pop("offset")
        if s.new_size:
            f["scope"]["size"] = s.new_size
    return d


def edits_for(tree, strats, min_conf, fixed_changes):
    """relpath -> new text."""
    texts = {}
    added = {}
    side = tree.side

    def editor(rel):
        if rel not in texts:
            if (tree.root / rel).is_file():
                texts[rel] = Editor((tree.root / rel).read_text())
            else:
                base = tree.base_of.get(rel)
                texts[rel] = Editor(new_overlay(side, base, f"sigport: {side} overlay of {base}"))
        return texts[rel]

    for s in strats:
        if s.new_pattern is None or s.conf < min_conf:
            continue
        changed = (s.new_pattern != s.pattern or (s.new_offset or 0) != s.offset or s.new_size)
        if not changed:
            continue
        dest, origin = tree.dest[s.name], tree.origin[s.name]
        ed = editor(dest)
        if dest != origin:
            # copy the whole symbol into the overlay once, then edit it there
            if (dest, s.name) not in added:
                spans_has = s.name in json.loads(ed.text).get("symbols", {})
                if not spans_has:
                    ed.add_symbol(s.name, tree.doc["symbols"][s.name])
                added[(dest, s.name)] = True
        base = ("symbols", s.name, "find", s.idx)
        ed.set(base + ("pattern",), s.new_pattern)
        if s.new_offset != s.offset:
            ed.set(base + ("offset",), s.new_offset)
        if s.new_size:
            ed.set(base + ("scope", "size"), s.new_size)
    for (cname, rel), value in fixed_changes.items():
        editor(rel).set(("constants", cname, "value"), value)
    return {rel: ed.text for rel, ed in texts.items()}


def corroborate(porter, cname, old_v, new_v, used_fns, limit=8):
    """Other matched functions using old_v as a displacement/immediate: what does the aligned new instruction use?"""
    a, b, M = porter.a, porter.b, porter.M
    if old_v is None or old_v < 0x100:
        return None
    votes = {}
    n = 0
    for fo, f in a.feat.items():
        if n >= limit:
            break
        if fo in used_fns or fo not in M.m:
            continue
        if old_v not in f["disps"] and old_v not in f["imms"]:
            continue
        if f["ninsn"] > 3000:
            continue
        fn = M.m[fo]
        if M.how[fo][0] == "identical":
            votes[old_v] = votes.get(old_v, 0) + 1
            n += 1
            continue
        lo, ln = porter.finsns(0, fo), porter.finsns(1, fn)
        mp = porter.falign(fo, fn)
        for k, ins in enumerate(lo):
            for role in ("disp", "imm"):
                off, size = field(ins, role)
                if not off or size not in (1, 2, 4):
                    continue
                if read_value(a.img, ins["a"] + off, size, "d32") != old_v:
                    continue
                if k not in mp or mp[k][1] < 0.6:
                    continue
                ins2 = ln[mp[k][0]]
                off2, size2 = field(ins2, role)
                if ins2["m"] != ins["m"] or not off2:
                    continue
                v2 = read_value(b.img, ins2["a"] + off2, size2, "d32")
                votes[v2] = votes.get(v2, 0) + 1
        n += 1
    return votes


def build_report(porter, final_syms, final_consts, final_feats, min_conf, old_final=None):
    P = porter
    by_sym = {}
    for s in P.strats:
        by_sym.setdefault(s.name, []).append(s)
    syms = {}
    for name, ss in by_sym.items():
        old = P.old_syms.get(name, {})
        fin = final_syms.get(name, {})
        best = max((s for s in ss if s.new_pattern), key=lambda s: s.conf, default=None)
        conf = 0.0
        if name.startswith("@"):
            conf = min(((s.conf if s.new_pattern else 0.0) for s in ss if s.old_hit is not None), default=0.0)
        else:
            src = fin.get("source", "")
            if src.startswith("captured by "):
                src = src[len("captured by "):]
            chosen = next((s for s in P.strats if s.tag == src), None)
            conf = chosen.conf if chosen and chosen.new_pattern else (best.conf if best else 0.0)
            votes = P.rva_votes.get(name, [])
            if len({v for v, _, c in votes if c >= min_conf}) > 1:
                conf *= 0.5
        cand = None
        if best is not None and not name.startswith("@"):
            cand = hex(best.site + (best.new_offset or 0))
        syms[name] = {
            "old": hex(old["rva"]) if old.get("rva") is not None else None,
            "new": hex(fin["rva"]) if fin.get("ok") and "rva" in fin else None,
            "candidate": cand,
            "ok_old": bool(old.get("ok")), "ok_new": bool(fin.get("ok")),
            "confidence": round(conf if fin.get("ok") else 0.0, 3),
            "method": "+".join(sorted({s.method for s in ss if s.method})) or None,
            "source": fin.get("source", ""), "error": fin.get("error", "") if not fin.get("ok") else "",
            "strategies": [s.row() for s in ss],
        }
    # captured-only symbols (rel32 targets without own definition)
    for name, fin in final_syms.items():
        if name in syms:
            continue
        votes = P.rva_votes.get(name, [])
        conf = max((c for _, _, c in votes), default=0.0)
        old = P.old_syms.get(name, {})
        syms[name] = {"old": hex(old["rva"]) if old.get("rva") is not None else None,
                      "new": hex(fin["rva"]) if fin.get("ok") else None, "ok_old": bool(old.get("ok")),
                      "ok_new": bool(fin.get("ok")), "confidence": round(conf if fin.get("ok") else 0.0, 3),
                      "method": "captured", "source": fin.get("source", ""),
                      "error": fin.get("error", "") if not fin.get("ok") else "", "strategies": []}
    consts = {}
    cap_conf = {}
    cap_fn = {}
    for s in P.strats:
        for hname, kind, _at, _w, v in s.holes_new:
            if kind != "rel32" and s.new_pattern and s.conf >= min_conf:
                cap_conf.setdefault(hname, []).append((v, s.conf, s.tag))
                if s.fo is not None:
                    cap_fn.setdefault(hname, set()).add(s.fo)
    for name, fin in final_consts.items():
        old = P.old_consts.get(name, {})
        caps = cap_conf.get(name, [])
        vals = {v for v, _, _ in caps}
        nv = fin.get("value") if fin.get("ok") else None
        ov = old.get("value") if old.get("ok") else None
        conf = 0.0
        if fin.get("ok"):
            agree = [c for v, c, _ in caps if v == nv]
            fns = cap_fn.get(name, set())
            if len(agree) >= 2 and len(fns) >= 2:
                conf = max(max(agree), 0.97)
            elif agree:
                conf = max(agree)
            elif "value" in P.doc.get("constants", {}).get(name, {}):
                # fixed by definition and nothing captures it: carried over unverified (the lab tests check it)
                conf = min_conf if old.get("ok") else 0.5
            if len(vals) > 1:
                conf = 0.0
        corr = None
        if fin.get("ok") and ov is not None and nv is not None and len(cap_fn.get(name, ())) < 2:
            corr = corroborate(P, name, ov, nv, cap_fn.get(name, set()))
            if corr:
                # other uses of the same number are evidence only when the number is distinctive; small struct
                # offsets recur in unrelated structs, so they can only add confidence, never take it away
                agree_c = corr.get(nv, 0)
                other = sum(c for v, c in corr.items() if v != nv)
                if agree_c >= 2 and agree_c >= 2 * other:
                    conf = max(conf, min(0.97, conf + 0.02 * agree_c))
                elif ov >= 0x10000 and other > agree_c and other >= 2:
                    conf *= 0.8
        consts[name] = {"old": hex(ov) if ov is not None else None, "new": hex(nv) if nv is not None else None,
                        "delta": (f"{nv - ov:+#x}" if (nv is not None and ov is not None and nv != ov) else None),
                        "ok_old": bool(old.get("ok")), "ok_new": bool(fin.get("ok")),
                        "captures": [{"value": hex(v), "confidence": round(c, 3), "by": t} for v, c, t in caps],
                        "votes": sum(1 for v, _, _ in caps if v == nv),
                        "corroboration": {hex(k): v for k, v in corr.items()} if corr else None,
                        "confidence": round(conf, 3), "error": fin.get("error", "") if not fin.get("ok") else ""}
    return syms, consts


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--old", required=True, help="exe on which the definitions resolve")
    ap.add_argument("--new", required=True, help="exe to port to")
    ap.add_argument("--defs", help="<tree>/<side>.json (default: the repo's sigdb/symbols/<side>.json)")
    ap.add_argument("--side", choices=["server", "client"], help="default: from --defs, else from the exe name")
    ap.add_argument("--out", help="output folder (default .out/sigport/<side>-<old8>-<new8>)")
    ap.add_argument("--cache", default=str(DEFAULT_CACHE))
    ap.add_argument("--min-confidence", type=float, default=0.8)
    ap.add_argument("--no-exact", action="store_true", help="ablation: never use exact pattern hits")
    ap.add_argument("--no-generalize", action="store_true",
                    help="patterns from the new exe only (default: prefer patterns that also resolve on the old exe)")
    ap.add_argument("--regenerate-all", action="store_true", help="regenerate patterns that still resolve, too")
    ap.add_argument("--steam-build", type=int)
    ap.add_argument("--svn", type=int)
    ap.add_argument("--record-to", help="also write the candidate record into this builds folder when it has none "
                                        "for the new sha (e.g. sigdb/builds)")
    ap.add_argument("--quiet", action="store_true")
    a = ap.parse_args(argv)
    log = (lambda *_: None) if a.quiet else (lambda m: print(f"sigport: {m}", file=sys.stderr))
    side = a.side
    if a.defs is None:
        side = side or ("client" if Path(a.new).name.lower() == "enshrouded.exe" else "server")
        a.defs = str(REPO / "sigdb" / "symbols" / f"{side}.json")
    side = side or Path(a.defs).stem
    try:
        tree = Tree(a.defs)
    except (OSError, json.JSONDecodeError) as e:
        print(f"sigport: cannot load {a.defs}: {e}", file=sys.stderr)
        return 2
    for p in tree.problems:
        log(f"definitions: {p}")
    old = Exe(a.old, a.cache, quiet=a.quiet)
    new = Exe(a.new, a.cache, quiet=a.quiet)
    out = Path(a.out) if a.out else REPO / ".out" / "sigport" / f"{side}-{old.sha[:8]}-{new.sha[:8]}"
    out.mkdir(parents=True, exist_ok=True)
    opts = a
    P = Porter(old, new, tree, opts, log).run()
    # fixed constants whose captures moved
    doc_new = patched_doc(tree.doc, P.strats, a.min_confidence)
    syms, consts, feats = Resolver(new.image, copy.deepcopy(doc_new)).run()
    fixed_changes = {}
    for cname, d in doc_new.get("constants", {}).items():
        if "value" not in d or consts.get(cname, {}).get("ok"):
            continue
        caps = {s.values[cname] for s in P.strats if cname in s.values and s.new_pattern}
        if len(caps) == 1:
            v = next(iter(caps))
            fixed_changes[(cname, tree.cdest.get(cname, tree.main))] = v
            log(f"fixed constant {cname}: {d.get('value')} -> {v} (every capture agrees)")
            d["value"] = v
    if fixed_changes:
        syms, consts, feats = Resolver(new.image, copy.deepcopy(doc_new)).run()
    old_check = Resolver(old.image, copy.deepcopy(doc_new)).run()
    rsyms, rconsts = build_report(P, syms, consts, feats, a.min_confidence)
    # files: complete patched tree + diff
    new_texts = edits_for(tree, P.strats, a.min_confidence, fixed_changes)
    tdir = out / "tree"
    if tdir.exists():
        shutil.rmtree(tdir)
    for rel in tree.files():
        dst = tdir / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_text(new_texts.get(rel, (tree.root / rel).read_text()))
    for rel, text in new_texts.items():
        dst = tdir / rel
        if not dst.exists():
            dst.parent.mkdir(parents=True, exist_ok=True)
            dst.write_text(text)
    try:
        prefix = str(tree.root.relative_to(REPO))
    except ValueError:
        prefix = str(tree.root).lstrip("/")
    diff = []
    for rel in sorted(new_texts):
        src = tree.root / rel
        before = src.read_text().splitlines(True) if src.exists() else []
        diff += difflib.unified_diff(before, new_texts[rel].splitlines(True),
                                     fromfile=f"a/{prefix}/{rel}" if src.exists() else "/dev/null",
                                     tofile=f"b/{prefix}/{rel}")
    (out / "symbols.patch").write_text("".join(diff))
    # sanity: the written tree resolves like the in-memory patched doc
    from sides import merged  # noqa: E402
    chk_syms, chk_consts, _ = Resolver(new.image, merged(tdir / tree.main)).run()
    mism = [n for n in syms if syms[n].get("ok") != chk_syms.get(n, {}).get("ok") or
            syms[n].get("rva") != chk_syms.get(n, {}).get("rva")]
    # summary
    real = {n: r for n, r in rsyms.items() if not n.startswith("@")}
    need = [n for n, r in real.items() if r["ok_old"]]
    ported = [n for n in need if real[n]["ok_new"] and real[n]["confidence"] >= a.min_confidence]
    low = [n for n in need if real[n]["ok_new"] and real[n]["confidence"] < a.min_confidence]
    lost = [n for n in need if not real[n]["ok_new"]]
    cneed = [n for n, r in rconsts.items() if r["ok_old"]]
    cported = [n for n in cneed if rconsts[n]["ok_new"] and rconsts[n]["confidence"] >= a.min_confidence]
    clow = [n for n in cneed if rconsts[n]["ok_new"] and rconsts[n]["confidence"] < a.min_confidence]
    clost = [n for n in cneed if not rconsts[n]["ok_new"]]
    changed = [s.tag for s in P.strats if s.new_pattern and s.conf >= a.min_confidence and
               (s.new_pattern != s.pattern or (s.new_offset or 0) != s.offset or s.new_size)]
    feats_old = P.old_feats
    summary = {
        "symbols": {"resolved_old": len(need), "ported": len(ported), "low_confidence": low, "lost": lost},
        "constants": {"resolved_old": len(cneed), "ported": len(cported), "low_confidence": clow, "lost": clost},
        "features": {"on_old": sorted(k for k, v in feats_old.items() if v),
                     "on_new": sorted(k for k, v in feats.items() if v),
                     "lost": sorted(k for k, v in feats_old.items() if v and not feats.get(k))},
        "strategies_changed": changed,
        "defs_unchanged": not changed and not fixed_changes,
        "fixed_constants_changed": {k[0]: hex(v) for k, v in fixed_changes.items()},
        "unverified_fixed": sorted(n for n in cneed if not rconsts[n]["captures"]
                                   and "value" in tree.doc.get("constants", {}).get(n, {})),
        "still_resolves_on_old": sum(1 for n, r in old_check[0].items() if r.get("ok") and not n.startswith("@")),
        "tree_check_mismatches": mism,
        "delta_families": delta_families(rconsts),
        "matcher": P.M.stats(),
        "complete": not (low or lost or clow or clost),
    }
    report = {"tool": "sigport port", "version": TOOL_VERSION, "date": datetime.datetime.now().isoformat(timespec="seconds"),
              "side": side, "defs": str(Path(a.defs).resolve()),
              "old": {"exe": str(Path(a.old).resolve()), "sha256": old.sha},
              "new": {"exe": str(Path(a.new).resolve()), "sha256": new.sha},
              "options": {"min_confidence": a.min_confidence, "no_exact": a.no_exact, "no_generalize": a.no_generalize,
                          "regenerate_all": a.regenerate_all},
              "summary": summary, "symbols": rsyms, "constants": rconsts,
              "features": {k: bool(v) for k, v in sorted(feats.items())}}
    (out / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    (out / "report.txt").write_text(render_text(report))
    # candidate build record
    raw = Path(a.new).read_bytes()
    cv, pdb = codeview(raw)
    rec = {"sha256": new.sha, "side": side, "codeview": cv, "pdb": pdb}
    if a.steam_build:
        rec["steam_build"] = a.steam_build
    if a.svn:
        rec["svn"] = a.svn
    rec["verified"] = False
    rec["note"] = (f"sigport candidate {report['date']}: ported from {old.sha[:8]}; {len(ported)}/{len(need)} symbols and "
                   f"{len(cported)}/{len(cneed)} constants at confidence >= {a.min_confidence}; not verified at runtime"
                   + ("" if summary["defs_unchanged"] else f"; needs symbols.patch ({len(changed)} strategies)"))
    rec["sigport"] = {"from": old.sha, "report": str((out / "report.json").resolve()),
                      "defs_unchanged": summary["defs_unchanged"], "complete": summary["complete"],
                      "low_confidence": low + clow, "lost": lost + clost}
    rec["features"] = {k: bool(v) for k, v in sorted(feats.items())}
    rec["symbols"] = {k: hex(v["rva"]) for k, v in sorted(syms.items()) if not k.startswith("@") and v.get("ok")}
    rec["constants"] = {k: hex(v["value"]) for k, v in sorted(consts.items()) if v.get("ok")}
    bdir = out / "builds" / side
    bdir.mkdir(parents=True, exist_ok=True)
    (bdir / f"{new.sha}.json").write_text(json.dumps(rec, indent=2) + "\n")
    if a.record_to:
        dst = Path(a.record_to) / side / f"{new.sha}.json"
        if dst.exists():
            log(f"{dst} exists; not replaced")
        else:
            dst.parent.mkdir(parents=True, exist_ok=True)
            dst.write_text(json.dumps(rec, indent=2) + "\n")
            log(f"candidate record {dst}")
    log(f"{len(ported)}/{len(need)} symbols, {len(cported)}/{len(cneed)} constants ported "
        f"(low confidence: {len(low) + len(clow)}, lost: {len(lost) + len(clost)}); "
        f"{len(changed)} strategies changed; report {out / 'report.txt'}")
    if mism:
        log(f"WARNING: the written tree resolves differently for {mism}")
    print(out)
    return 0 if summary["complete"] else 1


def delta_families(consts):
    """Constants that moved, grouped by how far: a struct that grew shows up as one family (+0x1c00 world fields)."""
    fam = {}
    for n, x in consts.items():
        if x.get("delta"):
            fam.setdefault(x["delta"], []).append(n)
    return {d: sorted(v) for d, v in sorted(fam.items(), key=lambda kv: -len(kv[1]))}


def render_text(r):
    s = r["summary"]
    L = [f"sigport port ({r['side']}): {r['old']['sha256'][:12]} -> {r['new']['sha256'][:12]}  {r['date']}",
         f"  old {r['old']['exe']}", f"  new {r['new']['exe']}", f"  defs {r['defs']}", ""]
    L.append(f"symbols   {s['symbols']['ported']}/{s['symbols']['resolved_old']} ported; low confidence: "
             f"{', '.join(s['symbols']['low_confidence']) or '-'}; lost: {', '.join(s['symbols']['lost']) or '-'}")
    L.append(f"constants {s['constants']['ported']}/{s['constants']['resolved_old']} ported; low confidence: "
             f"{', '.join(s['constants']['low_confidence']) or '-'}; lost: {', '.join(s['constants']['lost']) or '-'}")
    L.append(f"features  lost: {', '.join(s['features']['lost']) or '-'}")
    L.append(f"patterns  {len(s['strategies_changed'])} strategies changed"
             + ("" if s["defs_unchanged"] else " (symbols.patch)") + f"; matcher {s['matcher']}")
    if s["fixed_constants_changed"]:
        L.append(f"fixed     {s['fixed_constants_changed']}")
    for d, names in s.get("delta_families", {}).items():
        L.append(f"moved     {d}: {len(names)} constant(s): {', '.join(names[:8])}{' ...' if len(names) > 8 else ''}")
    neither = sorted(n for n, x in list(r["symbols"].items()) + list(r["constants"].items())
                     if not x["ok_old"] and not x["ok_new"])
    if neither:
        L.append(f"neither   resolve on the old exe either (not ported): {', '.join(neither)}")
    L.append("")
    L.append(f"{'symbol':44s} {'old':>10s} {'new':>10s} conf  method")
    for n, x in sorted(r["symbols"].items()):
        if n.startswith("@") and x["ok_new"] and x["confidence"] >= r["options"]["min_confidence"]:
            continue
        if not x["ok_old"] and not x["ok_new"]:
            continue
        flag = "" if x["ok_new"] and x["confidence"] >= r["options"]["min_confidence"] else "  <-- " + (x["error"] or "low confidence")
        L.append(f"{n:44s} {x['old'] or '-':>10s} {x['new'] or '-':>10s} {x['confidence']:.2f}  {x['method'] or '-'}{flag}")
        for st in x["strategies"]:
            if st["pattern_changed"] or st["error"] or (st["notes"] and not x["ok_new"]):
                L.append(f"    {st['tag']}: {st['method'] or '-'} {st['confidence']:.2f}"
                         + (f" ERROR {st['error']}" if st["error"] else ""))
                if st["pattern_changed"]:
                    L.append(f"      - {st['old_pattern']}")
                    L.append(f"      + {st['new_pattern']}")
                for note in st["notes"]:
                    L.append(f"      . {note}")
    L.append("")
    L.append(f"{'constant':44s} {'old':>10s} {'new':>10s} conf  votes  delta")
    for n, x in sorted(r["constants"].items()):
        if not x["ok_old"] and not x["ok_new"]:
            continue
        flag = "" if x["ok_new"] and x["confidence"] >= r["options"]["min_confidence"] else "  <-- " + (x["error"] or "low confidence")
        corr = f"  corroborated {x['corroboration']}" if x["corroboration"] else ""
        L.append(f"{n:44s} {x['old'] or '-':>10s} {x['new'] or '-':>10s} {x['confidence']:.2f}  {x['votes']:5d}  "
                 f"{x['delta'] or ''}{corr}{flag}")
    return "\n".join(L) + "\n"


if __name__ == "__main__":
    sys.exit(main())
