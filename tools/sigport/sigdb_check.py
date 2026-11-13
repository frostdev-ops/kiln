#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Offline twin of kiln-core's sigdb resolver (native/core/src/sigdb.cpp).

Resolves sigdb/symbols/<side>.json against an executable on disk and prints symbols, constants and features, so a
definition change can be checked against several game builds before it ships. One rule more than the core: when a
symbol has several strategies, every one that matches must name the same address (Resolver.disagreeing).

    python3 tools/sigport/sigdb_check.py sigdb/symbols/server.json <enshrouded_server.exe> [--json out.json]
    python3 tools/sigport/sigdb_check.py sigdb/symbols/client.json <enshrouded.exe> [--json out.json]
"""
import argparse
import json
import re
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from peimg import Image  # noqa: E402

HOLE = re.compile(r"\{([^}:]+):(d8|d16|d32|s32|rel32)\}")
WIDTH = {"d8": 1, "d16": 2, "d32": 4, "s32": 4, "rel32": 4}


def parse(pattern):
    """-> (regex bytes, holes [(name, kind, offset)], length)"""
    out, holes, n = b"", [], 0
    for tok in re.findall(r"\{[^}]+\}|\?\?|\?|[0-9A-Fa-f]{2}", pattern):
        m = HOLE.fullmatch(tok)
        if m:
            w = WIDTH[m.group(2)]
            holes.append((m.group(1), m.group(2), n))
            out += b"." * w
            n += w
        elif tok.startswith("?"):
            out += b"."
            n += 1
        else:
            out += re.escape(bytes([int(tok, 16)]))
            n += 1
    return re.compile(out, re.S), holes, n


class Resolver:
    def __init__(self, image, doc):
        self.img, self.doc = image, doc
        self.text_lo = image.text_va
        self.text_hi = image.text_va + len(image.text)
        self.symbols, self.state = {}, {}
        self.const_votes, self.sym_votes = {}, {}

    def scan(self, rx, lo, hi, limit=2):
        hits = []
        i = lo
        while len(hits) < limit:  # overlapping matches, like the core's scanner
            m = rx.search(self.img.img, i, hi)
            if not m:
                break
            hits.append(m.start())
            i = m.start() + 1
        return hits

    def capture(self, holes, at, source):
        for name, kind, off in holes:
            raw = bytes(self.img.img[at + off: at + off + WIDTH[kind]])
            if kind == "rel32":
                v = at + off + 4 + struct.unpack("<i", raw)[0]
                self.sym_votes.setdefault(name, []).append((v, source))
                continue
            v = struct.unpack({"d8": "<B", "d16": "<H", "d32": "<I", "s32": "<i"}[kind], raw)[0]
            self.const_votes.setdefault(name, []).append((v, source))

    def resolve(self, name):
        st = self.state.get(name)
        if st == 2:
            return self.symbols[name]["ok"]
        if st == 1:
            self.symbols[name] = {"ok": False, "error": "dependency cycle"}
            return False
        self.state[name] = 1
        defn = self.doc["symbols"].get(name)
        r = {"ok": False, "error": ""}
        if defn is None:
            r["error"] = "no definition"
        else:
            probe = name.startswith("@")
            unique = 0
            for i, f in enumerate(defn["find"]):
                tag = f"{name}.find[{i}]"
                lo, hi = self.text_lo, self.text_hi
                if "scope" in f:
                    s = f["scope"]["symbol"]
                    if not self.resolve(s):
                        r["error"] += f"; {tag}: scope {s} unresolved"
                        continue
                    lo = self.symbols[s]["rva"]
                    hi = min(self.text_hi, lo + f["scope"]["size"])
                rx, holes, _ = parse(f["pattern"])
                hits = self.scan(rx, lo, hi)
                if len(hits) != 1:
                    r["error"] += f"; {tag}: {len(hits)}{'+' if len(hits) > 1 else ''} matches"
                    continue
                unique += 1
                self.capture(holes, hits[0], tag)
                if not probe:
                    r = {"ok": True, "rva": hits[0] + f.get("offset", 0), "source": tag}
                    break
            if probe:
                r = {"ok": unique == len(defn["find"]), "source": f"{unique}/{len(defn['find'])} strategies matched"}
        r["error"] = r.get("error", "").lstrip("; ")
        self.symbols[name] = r
        self.state[name] = 2
        return r["ok"]

    def disagreeing(self, name, defn, rva):
        """One rule more than the core has. The core takes the first strategy that matches once and looks no further;
        here every strategy of a resolved symbol that matches once must name the symbol's address, else the symbol
        fails. A later strategy is the one in use when the first cannot match (another engine's hook on the function's
        first bytes while the core scans live code), so a stale offset in it would show only there, as a hook in the
        wrong place: a pattern anchored in a function's body keeps matching when a game update changes the length of
        the code before it. run() asks this last, when every symbol and capture is settled: a scoped strategy is then
        judged whatever the order of the definitions, and no capture brings a failed symbol back. Nothing is captured
        or resolved from here."""
        for j, f in enumerate(defn["find"]):
            lo, hi = self.text_lo, self.text_hi
            if "scope" in f:
                scope = self.symbols.get(f["scope"]["symbol"], {})
                if not scope.get("ok"):
                    continue
                lo = scope["rva"]
                hi = min(self.text_hi, lo + f["scope"]["size"])
            hits = self.scan(parse(f["pattern"])[0], lo, hi)
            if len(hits) == 1 and hits[0] + f.get("offset", 0) != rva:
                return f"strategies disagree: {name} is {rva:#x}, {name}.find[{j}] gives {hits[0] + f.get('offset', 0):#x}"
        return ""

    def run(self):
        # Like the core (sigdb.cpp): a symbol captured by a rel32 hole may be another strategy's scope, so resolve,
        # promote the captured symbols and retry the failed definitions until nothing new resolves.
        while True:
            for name in self.doc["symbols"]:
                self.resolve(name)
            new = {}
            for name, votes in self.sym_votes.items():
                if name in self.doc["symbols"] or self.symbols.get(name, {}).get("ok"):
                    continue
                vals = {v for v, _ in votes}
                if len(vals) == 1:
                    new[name] = {"ok": True, "rva": votes[0][0], "source": "captured by " + votes[0][1]}
            if not new:
                break
            self.symbols.update(new)
            for name in new:
                self.state[name] = 2
            for name in self.doc["symbols"]:
                if not self.symbols.get(name, {}).get("ok"):
                    self.state.pop(name, None)
                    self.symbols.pop(name, None)
        for name, votes in self.sym_votes.items():
            r = self.symbols.get(name, {"ok": False})
            vals = {v for v, _ in votes}
            if len(vals) != 1:
                r = {"ok": False, "error": "captures disagree"}
            elif r.get("ok") and r["rva"] != votes[0][0]:
                r = {"ok": False, "error": f"pattern {r['rva']:#x} vs captured {votes[0][0]:#x}"}
            elif not r.get("ok"):
                r = {"ok": True, "rva": votes[0][0], "source": "captured by " + votes[0][1]}
            self.symbols[name] = r
        for name, defn in self.doc["symbols"].items():  # the rule the core does not have, last (see disagreeing)
            r = self.symbols.get(name, {})
            if name.startswith("@") or not r.get("ok") or len(defn["find"]) < 2:
                continue
            error = self.disagreeing(name, defn, r["rva"])
            if error:
                self.symbols[name] = {"ok": False, "error": error}
        consts = {}
        defs = dict(self.doc.get("constants", {}))
        for name in self.const_votes:
            defs.setdefault(name, {})
        for name, d in defs.items():
            votes = self.const_votes.get(name, [])
            vals = {v for v, _ in votes}
            if len(vals) > 1:
                consts[name] = {"ok": False, "error": "captures disagree: " + ", ".join(f"{v:#x}@{s}" for v, s in votes)}
            elif "value" in d:
                ok = not vals or d["value"] in vals
                consts[name] = {"ok": ok, "value": d["value"], "source": "fixed" + (f", {len(votes)} captures" if votes else "")}
            elif not votes or len(votes) < d.get("min_votes", 1):
                consts[name] = {"ok": False, "error": f"{len(votes)} captures, need {d.get('min_votes', 1)}"}
            else:
                consts[name] = {"ok": True, "value": votes[0][0], "source": f"{len(votes)} captures agree"}
        feats = {}
        for name, f in self.doc.get("features", {}).items():
            feats[name] = all(self.symbols.get(s, {}).get("ok") for s in f.get("symbols", [])) and all(
                consts.get(c, {}).get("ok") for c in f.get("constants", []))
        return self.symbols, consts, feats


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("definitions")
    ap.add_argument("exe")
    ap.add_argument("--json")
    a = ap.parse_args()
    # <side>.json + the per-service files the core would see in kiln/sigdb/<side>.d/ (duplicates are errors). The
    # client's .d set is composed from server.d + client.d overlays like the deploy does (tools/sigport/sides.py).
    from sides import merged  # noqa: E402
    doc = merged(a.definitions)
    syms, consts, feats = Resolver(Image(a.exe), doc).run()
    bad = 0
    for n, r in syms.items():
        if r["ok"]:
            print(f"  ok  {n:24s} {('%#x' % r['rva']) if 'rva' in r else '':10s} {r.get('source', '')}")
        else:
            bad += not n.startswith("@")
            print(f"FAIL  {n:24s} {r.get('error', '')}")
    for n, r in consts.items():
        print(f"  {'ok' if r['ok'] else 'FAIL'}  {n:24s} {('%#x' % r['value']) if r.get('ok') else r.get('error', '')}  {r.get('source', '')}")
        bad += not r["ok"]
    print("features: " + ", ".join(f"{n}={'on' if ok else 'OFF'}" for n, ok in feats.items()))
    if a.json:
        Path(a.json).write_text(json.dumps({"symbols": syms, "constants": consts, "features": feats}, indent=2))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
