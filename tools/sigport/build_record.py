#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes a build record sigdb/builds/<side>/<sha256>.json from an offline resolve of <side>.json (+ its .d set).

The core's policy "verified-only" installs hooks only when the record for the running exe says "verified": true, so a
new record starts unverified; flip it with --verified only after the runtime checks passed (docs/kiln/client.md).

    python3 tools/sigport/build_record.py client <enshrouded.exe> --steam-build 26404966 [--svn N]
    python3 tools/sigport/build_record.py client <enshrouded.exe> --verified --note "lab-verified ...: ..."
"""
import argparse
import hashlib
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from peimg import Image  # noqa: E402
from sides import merged  # noqa: E402
from sigdb_check import Resolver  # noqa: E402

REPO = Path(__file__).resolve().parents[2]


def codeview(raw):
    pe = struct.unpack_from("<I", raw, 0x3C)[0]
    opt = pe + 24
    dbg_va, dbg_size = struct.unpack_from("<II", raw, opt + 112 + 6 * 8)
    nsec = struct.unpack_from("<H", raw, pe + 6)[0]
    optsz = struct.unpack_from("<H", raw, pe + 20)[0]
    off = opt + optsz
    for _ in range(nsec):
        vsz, va, rsz, ptr = struct.unpack_from("<IIII", raw, off + 8)
        if va <= dbg_va < va + max(vsz, rsz):
            base = ptr + dbg_va - va
            for i in range(dbg_size // 28):
                typ, size, _rva, data = struct.unpack_from("<IIII", raw, base + i * 28 + 12)
                if typ == 2 and raw[data:data + 4] == b"RSDS":
                    g = raw[data + 4:data + 20]
                    d1, d2, d3 = struct.unpack_from("<IHH", g)
                    age = struct.unpack_from("<I", raw, data + 20)[0]
                    pdb = raw[data + 24:data + size].split(b"\0")[0].decode(errors="replace")
                    return f"{d1:08X}{d2:04X}{d3:04X}{g[8:].hex().upper()}-{age}", pdb.replace("\\", "/").split("/")[-1]
        off += 40
    return "", ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("side", choices=["server", "client"])
    ap.add_argument("exe")
    ap.add_argument("--steam-build", type=int)
    ap.add_argument("--svn", type=int)
    ap.add_argument("--verified", action="store_true")
    ap.add_argument("--note", default="")
    ap.add_argument("--out", help="default: sigdb/builds/<side>/<sha256>.json")
    a = ap.parse_args()
    raw = Path(a.exe).read_bytes()
    sha = hashlib.sha256(raw).hexdigest()
    cv, pdb = codeview(raw)
    syms, consts, feats = Resolver(Image(a.exe), merged(REPO / "sigdb" / "symbols" / f"{a.side}.json")).run()
    rec = {"sha256": sha, "side": a.side, "codeview": cv, "pdb": pdb}
    if a.steam_build:
        rec["steam_build"] = a.steam_build
    if a.svn:
        rec["svn"] = a.svn
    rec["verified"] = bool(a.verified)
    rec["note"] = a.note or ("offline resolve only (tools/sigport/build_record.py); not verified at runtime" if not a.verified else "")
    rec["features"] = {k: bool(v) for k, v in sorted(feats.items())}
    rec["symbols"] = {k: hex(v["rva"]) for k, v in sorted(syms.items()) if not k.startswith("@") and v.get("ok")}
    rec["constants"] = {k: hex(v["value"]) for k, v in sorted(consts.items()) if v.get("ok")}
    out = Path(a.out) if a.out else REPO / "sigdb" / "builds" / a.side / f"{sha}.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(rec, indent=2) + "\n")
    off = [k for k, v in rec["features"].items() if not v]
    print(f"{out}: {len(rec['symbols'])} symbols, {len(rec['constants'])} constants, "
          f"{len(rec['features']) - len(off)}/{len(rec['features'])} features on (off: {', '.join(off) or '-'}); "
          f"verified={rec['verified']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
