#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Server vs client: does each symbol resolve on both exes, and is it the same function?

A pattern that matches once on the client can still match the wrong function. This resolves both sides' definitions
(server.json + server.d, client.json + composed client.d, like the core), then compares every symbol found on both
sides instruction by instruction (capstone; registers kept, displacements >= 0x10000, rip-relative operands and branch
targets masked) over its .pdata function, and prints the similarity. Same engine build: identical functions give
1.000; register allocation or frame size changes show up as 0.6-0.95 and need a look (docs/re/client.md).

    python3 tools/sigport/sidecompare.py <enshrouded_server.exe> <enshrouded.exe> [--json out.json] [--prefix chat/]
"""
import argparse
import bisect
import difflib
import json
import re
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from peimg import Image  # noqa: E402
from sides import merged  # noqa: E402
from sigdb_check import Resolver  # noqa: E402

SYMBOLS = Path(__file__).resolve().parents[2] / "sigdb" / "symbols"


class Funcs:
    """Function bounds from .pdata (chained unwind entries folded into their primary)."""

    def __init__(self, img):
        self.img = img
        va, size = img.sections[".pdata"]
        rows = []
        for off in range(va, va + size, 12):
            b, e, u = struct.unpack_from("<III", img.img, off)
            if b == 0:
                break
            rows.append((b, e, u))
        rows.sort()
        self.rows = rows
        self.starts = [r[0] for r in rows]

    def bounds(self, rva):
        i = bisect.bisect_right(self.starts, rva) - 1
        if i < 0 or not (self.rows[i][0] <= rva < self.rows[i][1]):
            return None
        return self.rows[i][0], self.rows[i][1]


def norm(img, funcs, rva, cap=0x6000):
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    b = funcs.bounds(rva)
    start, end = (rva, rva + 0x400) if b is None else (rva, max(b[1], rva + 16))
    end = min(end, rva + cap)
    out = []
    for i in md.disasm(img.img[start:end], img.base + start):
        ops = re.sub(r"\[rip [+-] 0x[0-9a-f]+\]", "[rip+X]", i.op_str)
        if i.mnemonic == "call" or i.mnemonic.startswith("j"):
            ops = "T"
        ops = re.sub(r"0x[0-9a-f]{5,}", "K", ops)
        out.append(f"{i.mnemonic} {ops}")
        if i.mnemonic in ("ret", "int3") and b is None and len(out) > 4:
            break
    return out, b is not None


def resolve(definitions, exe):
    img = Image(exe)
    syms, consts, feats = Resolver(img, merged(definitions)).run()
    return img, syms, consts, feats


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("server_exe")
    ap.add_argument("client_exe")
    ap.add_argument("--json")
    ap.add_argument("--prefix", default="")
    a = ap.parse_args()
    simg, ssyms, sconsts, sfeats = resolve(SYMBOLS / "server.json", a.server_exe)
    cimg, csyms, cconsts, cfeats = resolve(SYMBOLS / "client.json", a.client_exe)
    sf, cf = Funcs(simg), Funcs(cimg)
    rows = []
    names = sorted(n for n in set(ssyms) | set(csyms) if not n.startswith("@") and n.startswith(a.prefix))
    for n in names:
        s, c = ssyms.get(n, {}), csyms.get(n, {})
        row = {"name": n, "server": s.get("ok", False), "client": c.get("ok", False)}
        if row["server"]:
            row["server_rva"] = hex(s["rva"])
        if row["client"]:
            row["client_rva"] = hex(c["rva"])
        if row["server"] and row["client"]:
            so, _ = norm(simg, sf, s["rva"])
            co, _ = norm(cimg, cf, c["rva"])
            row["similarity"] = round(difflib.SequenceMatcher(None, so, co, autojunk=False).ratio(), 3)
            row["instructions"] = [len(so), len(co)]
        rows.append(row)
    both = [r for r in rows if r["server"] and r["client"]]
    only_s = [r for r in rows if r["server"] and not r["client"]]
    only_c = [r for r in rows if r["client"] and not r["server"]]
    for r in rows:
        sim = r.get("similarity")
        flag = "" if sim is None or sim >= 0.95 else ("  <- check" if sim >= 0.6 else "  <- DIFFERENT")
        print(f"{r['name']:36s} server {r.get('server_rva', '-'):>9s}  client {r.get('client_rva', '-'):>9s}  "
              f"{'' if sim is None else f'{sim:.3f}'}{flag}")
    feats = sorted(set(sfeats) | set(cfeats))
    fs = [f for f in feats if sfeats.get(f)]
    fc = [f for f in fs if cfeats.get(f)]
    print(f"\nsymbols: {len(both)} on both sides, {len(only_s)} server only, {len(only_c)} client only; "
          f"{sum(1 for r in both if r['similarity'] >= 0.95)} of {len(both)} structurally identical (>= 0.95)")
    print(f"features on the server: {len(fs)}; also on the client: {len(fc)}; off on the client: "
          f"{', '.join(f for f in fs if not cfeats.get(f)) or '-'}; client only: "
          f"{', '.join(f for f in feats if cfeats.get(f) and f not in sfeats) or '-'}")
    if a.json:
        Path(a.json).write_text(json.dumps({"symbols": rows, "server_features": sfeats, "client_features": cfeats},
                                           indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
