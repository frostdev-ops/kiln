#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Function matching between two exes (old -> new), for sigport.

Tiers, each feeding the next:
 1. identical: reciprocal-unique normalised whole bodies (>= 32 bytes). Idea from ShroudForge's profile rebase
    (ShroudForge, modules/game-profile-discovery/rebase_profile_contract.cpp, MIT): bodies compared with rip-relative and external
    branch displacements masked; a body unique in both images is the same procedure.
 2. callsite: the callees of an identical pair correspond position by position (an identical body calls in the same
    order), which binds small and ambiguous callees; rip-relative data references bind globals the same way.
 3. anchors: a string, .rdata value, import, large immediate or mapped global referenced by exactly one function on
    each side votes for that pair; accepted when the pair is the best for both and similar enough.
 4. propagation: unmatched callees/callers of matched pairs, scored, reciprocal best.
 5. neighbourhood: MSVC keeps object files in link order, so an unmatched function sits between the images of its
    matched neighbours; candidates in that window are scored.
Similarity = weighted mnemonic 3-gram Jaccard, size ratio, mapped-callee agreement, strings, constants, imports.
"""
import bisect
import difflib
import re
from array import array

REG = re.compile(r"\b(?:[re]?(?:ax|bx|cx|dx|si|di|sp|bp)|[abcd][lh]|(?:si|di|sp|bp)l|r(?:[89]|1[0-5])[dwb]?|"
                 r"[xyz]mm\d+)\b")


def _shape(tok):
    """Token with registers masked and memory operands' register order ignored."""
    t = REG.sub("R", tok)
    return re.sub(r"\[R \+ R", "[R + R", t)


def _mn(f):
    a = array("H")
    a.frombytes(f["mn"])
    return a


def _jac(x, y):
    if not x and not y:
        return None
    u = len(x | y)
    return len(x & y) / u if u else 0.0


class Matcher:
    def __init__(self, a, b, log=None):
        self.a, self.b = a, b
        self.m, self.r, self.how = {}, {}, {}
        self.log = log or (lambda *_: None)
        self._grams = ({}, {})
        self._sets = ({}, {})
        self.data_map = {}

    # ---- similarity -----------------------------------------------------------------------------------------------
    def grams(self, side, s):
        c = self._grams[side]
        g = c.get(s)
        if g is None:
            e = self.a if side == 0 else self.b
            x = _mn(e.feat[s])
            if len(x) < 3:
                g = {("short",) + tuple(x)}
            else:
                g = {(x[i] << 22) | (x[i + 1] << 11) | x[i + 2] for i in range(len(x) - 2)}
            c[s] = g
        return g

    def sets(self, side, s):
        c = self._sets[side]
        v = c.get(s)
        if v is None:
            f = (self.a if side == 0 else self.b).feat[s]
            v = (set(f["strings"]), set(f["imms"]), set(f["imports"]), set(f["rvals"]))
            c[s] = v
        return v

    def sim(self, o, n):
        fa, fb = self.a.feat[o], self.b.feat[n]
        parts = []
        ga, gb = self.grams(0, o), self.grams(1, n)
        parts.append((0.35, _jac(ga, gb)))
        na, nb = max(fa["ninsn"], 1), max(fb["ninsn"], 1)
        parts.append((0.10, min(na, nb) / max(na, nb)))
        if fa["callees"] or fb["callees"]:
            mapped = [self.m.get(t) for t in fa["callees"]]
            known = [x for x in mapped if x is not None]
            if known:
                tb = set(fb["callees"])
                agree = sum(1 for x in known if x in tb) / len(known)
                cover = min(len(fa["callees"]), len(fb["callees"])) / max(len(fa["callees"]), len(fb["callees"]))
                parts.append((0.20, 0.8 * agree + 0.2 * cover))
            else:
                la, lb = len(fa["callees"]), len(fb["callees"])
                parts.append((0.10, min(la, lb) / max(la, lb)))
        sa, sb = self.sets(0, o), self.sets(1, n)
        for w, x, y in ((0.15, sa[0], sb[0]), (0.10, sa[1], sb[1]), (0.05, sa[2], sb[2]), (0.05, sa[3], sb[3])):
            j = _jac(x, y)
            if j is not None:
                parts.append((w, j))
        tw = sum(w for w, _ in parts)
        return sum(w * v for w, v in parts) / tw

    # ---- bookkeeping ----------------------------------------------------------------------------------------------
    def add(self, o, n, how, score):
        if o in self.m or n in self.r:
            return False
        self.m[o], self.r[n] = n, o
        self.how[o] = (how, round(score, 3))
        return True

    def stats(self):
        out = {}
        for h, _ in self.how.values():
            out[h] = out.get(h, 0) + 1
        return out

    # ---- tiers ----------------------------------------------------------------------------------------------------
    def identical(self):
        ia, ib = {}, {}
        for s, f in self.a.feat.items():
            ia.setdefault(f["hash"], []).append(s)
        for s, f in self.b.feat.items():
            ib.setdefault(f["hash"], []).append(s)
        self.hash_a, self.hash_b = ia, ib
        for h, v in ia.items():
            w = ib.get(h)
            if len(v) == 1 and w and len(w) == 1 and self.a.feat[v[0]]["size"] >= 32:
                self.add(v[0], w[0], "identical", 1.0)

    def callsites(self):
        """Callees and globals of identical bodies, position by position; conflicting votes cancel."""
        votes, dvotes = {}, {}
        for o, n in list(self.m.items()):
            if self.how[o][0] not in ("identical", "callsite"):
                continue
            fa, fb = self.a.feat[o], self.b.feat[n]
            if len(fa["callees"]) == len(fb["callees"]):
                for x, y in zip(fa["callees"], fb["callees"]):
                    if x in self.a.feat and y in self.b.feat:
                        votes.setdefault(x, set()).add(y)
            if len(fa["data"]) == len(fb["data"]):
                for x, y in zip(fa["data"], fb["data"]):
                    dvotes.setdefault(x, set()).add(y)
        added = 0
        rev = {}
        for x, ys in votes.items():
            if len(ys) == 1:
                rev.setdefault(next(iter(ys)), set()).add(x)
        for x, ys in votes.items():
            if len(ys) != 1:
                continue
            y = next(iter(ys))
            if len(rev.get(y, ())) != 1 or x in self.m or y in self.r:
                continue
            fa, fb = self.a.feat[x], self.b.feat[y]
            if fa["hash"] == fb["hash"] or self.sim(x, y) >= 0.3:
                added += self.add(x, y, "callsite", 1.0 if fa["hash"] == fb["hash"] else self.sim(x, y))
        drev = {}
        for x, ys in dvotes.items():
            if len(ys) == 1:
                drev.setdefault(next(iter(ys)), set()).add(x)
        for x, ys in dvotes.items():
            if len(ys) == 1 and len(drev.get(next(iter(ys)), ())) == 1:
                self.data_map[x] = next(iter(ys))
        return added

    def _tokens(self, side, s):
        e = self.a if side == 0 else self.b
        f = e.feat[s]
        t = {("s", x) for x in f["strings"]}
        t |= {("r", x) for x in f["rvals"] if x not in (b"\0" * 8,)}
        t |= {("i", x) for x in f["imms"] if x >= 0x10000}
        t |= {("m", x) for x in f["imports"]}
        if side == 0:
            t |= {("d", self.data_map[x]) for x in f["data"] if x in self.data_map}
        else:
            t |= {("d", x) for x in f["data"]}
        return t

    def anchors(self):
        ta, tb = {}, {}
        for s in self.a.feat:
            if s in self.m:
                continue
            for t in self._tokens(0, s):
                ta.setdefault(t, []).append(s)
        for s in self.b.feat:
            if s in self.r:
                continue
            for t in self._tokens(1, s):
                tb.setdefault(t, []).append(s)
        cand = {}
        for t, v in ta.items():
            w = tb.get(t)
            if len(v) == 1 and w and len(w) == 1:
                cand[(v[0], w[0])] = cand.get((v[0], w[0]), 0) + 1
        return self._accept(cand, "anchor", lambda votes, s: s >= (0.45 if votes >= 2 else 0.6))

    def _accept(self, cand, how, ok):
        scored = []
        for (o, n), votes in cand.items():
            if o in self.m or n in self.r:
                continue
            scored.append((self.sim(o, n), votes, o, n))
        best_a, best_b = {}, {}
        for s, v, o, n in scored:
            if s > best_a.get(o, (-1,))[0]:
                best_a[o] = (s, n)
            if s > best_b.get(n, (-1,))[0]:
                best_b[n] = (s, o)
        added = 0
        for s, v, o, n in sorted(scored, reverse=True):
            if best_a[o][1] == n and best_b[n][1] == o and ok(v, s):
                added += self.add(o, n, how, s)
        return added

    def propagate(self, rounds=8):
        total = 0
        for _ in range(rounds):
            cand = {}
            for o, n in list(self.m.items()):
                fa, fb = self.a.feat[o], self.b.feat[n]
                for xa, xb in ((fa["callees"], fb["callees"]),
                               (self.a.callers.get(o, ()), self.b.callers.get(n, ()))):
                    A = list(dict.fromkeys(t for t in xa if t in self.a.feat and t not in self.m))
                    B = list(dict.fromkeys(t for t in xb if t in self.b.feat and t not in self.r))
                    if not A or not B or len(A) * len(B) > 900:
                        continue
                    single = len(A) == 1 and len(B) == 1
                    for x in A:
                        for y in B:
                            cand[(x, y)] = cand.get((x, y), 0) + (3 if single else 1)
            added = self._accept(cand, "propagated", lambda votes, s: s >= 0.55 or (votes >= 3 and s >= 0.4))
            total += added
            if not added:
                break
        return total

    def neighbourhood(self, window=40):
        sa = sorted(self.a.feat)
        sb = sorted(self.b.feat)
        added = 0
        matched = [s for s in sa if s in self.m]
        for i, o in enumerate(sa):
            if o in self.m:
                continue
            k = bisect.bisect_left(matched, o)
            lo_o = matched[k - 1] if k > 0 else None
            hi_o = matched[k] if k < len(matched) else None
            lo = self.m[lo_o] if lo_o is not None else self.b.text_lo
            hi = self.m[hi_o] if hi_o is not None else self.b.text_hi
            if hi <= lo:
                continue
            j0, j1 = bisect.bisect_right(sb, lo), bisect.bisect_left(sb, hi)
            cands = [s for s in sb[j0:j1] if s not in self.r]
            if not cands or len(cands) > window:
                continue
            olds = [s for s in sa[bisect.bisect_right(sa, lo_o or 0):bisect.bisect_left(sa, hi_o or 1 << 40)]
                    if s not in self.m]
            sc = sorted(((self.sim(o, n), n) for n in cands), reverse=True)
            s, n = sc[0]
            second = sc[1][0] if len(sc) > 1 else 0.0
            if (s >= 0.5 and s - second >= 0.05) or (len(cands) == 1 and len(olds) == 1 and s >= 0.35):
                added += self.add(o, n, "neighbourhood", s)
                matched.insert(bisect.bisect_left(matched, o), o)
        return added

    def run(self):
        self.identical()
        self.log(f"match: identical {len(self.m)}")
        for _ in range(4):
            if not self.callsites():
                break
        self.log(f"match: + callsite -> {len(self.m)}")
        self.anchors()
        self.log(f"match: + anchors -> {len(self.m)}")
        self.propagate()
        self.log(f"match: + propagated -> {len(self.m)}")
        for _ in range(3):
            a = self.neighbourhood()
            p = self.propagate()
            self.callsites()
            if not a and not p:
                break
        self.log(f"match: + neighbourhood -> {len(self.m)} of {len(self.a.feat)} ({self.stats()})")
        return self

    # ---- on demand ------------------------------------------------------------------------------------------------
    def best_for(self, o, limit=None, region=None):
        """Scored candidates for an unmatched old function: its neighbourhood window, else region (b-rva range)."""
        sa = sorted(s for s in self.m)
        k = bisect.bisect_left(sa, o)
        lo = self.m[sa[k - 1]] if k > 0 else self.b.text_lo
        hi = self.m[sa[k]] if k < len(sa) else self.b.text_hi
        if region:
            lo, hi = region
        sb = sorted(self.b.feat)
        j0, j1 = bisect.bisect_left(sb, lo), bisect.bisect_left(sb, hi)
        cands = [n for n in sb[j0:j1] if n not in self.r]
        if limit and len(cands) > limit:
            return []
        return sorted(((self.sim(o, n), n) for n in cands), reverse=True)


def align(xs, ys):
    """Index map xs -> ys for two token lists: equal blocks, then equal-length replaced blocks position by position,
    then a mnemonic-only pass inside the remaining replaced blocks."""
    out = {}
    sm = difflib.SequenceMatcher(None, xs, ys, autojunk=False)
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag == "equal":
            for k in range(i2 - i1):
                out[i1 + k] = (j1 + k, 1.0)
        elif tag == "replace":
            if i2 - i1 == j2 - j1:
                for k in range(i2 - i1):
                    x, y = xs[i1 + k], ys[j1 + k]
                    if _shape(x) == _shape(y):
                        q = 0.9   # same instruction, other registers (register allocation)
                    elif x.split(" ", 1)[0] == y.split(" ", 1)[0]:
                        q = 0.7
                    else:
                        q = 0.4
                    out[i1 + k] = (j1 + k, q)
            else:
                mx = [x.split(" ", 1)[0] for x in xs[i1:i2]]
                my = [y.split(" ", 1)[0] for y in ys[j1:j2]]
                sm2 = difflib.SequenceMatcher(None, mx, my, autojunk=False)
                for t2, a1, a2, b1, b2 in sm2.get_opcodes():
                    if t2 == "equal":
                        for k in range(a2 - a1):
                            x, y = xs[i1 + a1 + k], ys[j1 + b1 + k]
                            out[i1 + a1 + k] = (j1 + b1 + k, 0.85 if _shape(x) == _shape(y) else 0.6)
    return out
