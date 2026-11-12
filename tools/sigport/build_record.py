#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes a build record sigdb/builds/<side>/<sha256>.json from an offline resolve of <side>.json (+ its .d set).

The core's policy "verified-only" installs hooks only when the record for the running exe says "verified": true, so a
new record starts unverified; flip it with --verified only after the runtime checks passed (docs/kiln/client.md).
The record lists every name sigdb declares, per service, each with a level (sigdb/builds/README.md).

    python3 tools/sigport/build_record.py client <enshrouded.exe> --steam-build 26404966 [--svn N]
    python3 tools/sigport/build_record.py client <enshrouded.exe> --verified --note "lab-verified ...: ..."
"""
import argparse
import datetime
import hashlib
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from peimg import Image  # noqa: E402
from sides import SECTIONS, compose, merged  # noqa: E402
from sigdb_check import HOLE, Resolver  # noqa: E402

REPO = Path(__file__).resolve().parents[2]
SYMBOLS = REPO / "sigdb" / "symbols"
BUILDS = REPO / "sigdb" / "builds"
RECORD = 2
LEVELS = ("static", "lab", "seen")              # what a service's names rest on, weakest first
NAME_LEVELS = LEVELS + ("unresolved",)          # a declared name that does not match this exe
CORE = "core"                                   # the service key of sigdb/symbols/<side>.json


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


# ---- what sigdb declares ----

def definition_files(side, symbols_dir=SYMBOLS):
    """[(service, doc)] in the core's load order: <side>.json as "core", then each composed <side>.d file by stem."""
    symbols_dir = Path(symbols_dir)
    files = [(CORE, json.loads((symbols_dir / f"{side}.json").read_text()))]
    files += [(Path(name).stem, doc) for name, doc, _src in compose(side, symbols_dir)]
    return files


def declared(side, symbols_dir=SYMBOLS):
    """{section: {name: service}}: every declared name once, under the file that declares it.

    A key under symbols/constants/features declares a name; the first file (core, then the .d files by name) with the
    key owns it. A name captured by a pattern hole ({name:rel32} a symbol, {name:d8|d16|d32|s32} a constant) and not a
    key anywhere belongs to the first file whose pattern captures it."""
    files = definition_files(side, symbols_dir)
    owner = {s: {} for s in SECTIONS}
    for svc, doc in files:
        for section in SECTIONS:
            for name in doc.get(section, {}):
                owner[section].setdefault(name, svc)
    for svc, doc in files:
        for defn in doc.get("symbols", {}).values():
            for f in defn.get("find", []):
                for m in HOLE.finditer(f.get("pattern", "")):
                    owner["symbols" if m.group(2) == "rel32" else "constants"].setdefault(m.group(1), svc)
    return owner


def declared_counts(owner):
    return {s: len(owner[s]) for s in SECTIONS}


# ---- the offline resolve ----

def resolve(side, exe, symbols_dir=SYMBOLS):
    """(syms, consts, feats, doc) of the offline resolver over <side>.json + its composed .d set."""
    doc = merged(Path(symbols_dir) / f"{side}.json")
    syms, consts, feats = Resolver(Image(str(exe)), doc).run()
    return syms, consts, feats, doc


def flat_maps(syms, consts, feats):
    """The format-1 maps the core and tools read: named resolved symbols (rva), resolved constants, every feature."""
    return ({k: bool(v) for k, v in sorted(feats.items())},
            {k: hex(v["rva"]) for k, v in sorted(syms.items()) if not k.startswith("@") and v.get("ok")},
            {k: hex(v["value"]) for k, v in sorted(consts.items()) if v.get("ok")})


def offline_state(owner, syms, consts, feats, doc):
    """{(section, name): (resolved, why)} for every declared name."""
    out = {}
    for name in owner["symbols"]:
        r = syms.get(name)
        if r is None:
            out[("symbols", name)] = (False, "not captured: no pattern that captures it matched")
        else:
            out[("symbols", name)] = (bool(r.get("ok")), r.get("error") or "no match")
    for name in owner["constants"]:
        r = consts.get(name)
        if r is None:
            out[("constants", name)] = (False, "not captured: no pattern that captures it matched")
        else:
            out[("constants", name)] = (bool(r.get("ok")), r.get("error") or "no match")
    for name in owner["features"]:
        f = doc.get("features", {}).get(name, {})
        need = [s for s in f.get("symbols", []) if not syms.get(s, {}).get("ok")]
        need += [c for c in f.get("constants", []) if not consts.get(c, {}).get("ok")]
        out[("features", name)] = (bool(feats.get(name)), "needs " + ", ".join(need) if need else "off")
    return out


def services_static(owner, state, date, evidence):
    """The services map of a fresh record: every resolved name at "static", the rest "unresolved" with the reason."""
    svcs = {}
    for section in SECTIONS:
        for name, svc in owner[section].items():
            e = svcs.setdefault(svc, {})
            ok, why = state[(section, name)]
            e.setdefault(section, {})[name] = "static" if ok else "unresolved"
            if not ok:
                e.setdefault("unresolved", {})[name] = why
    return {svc: service_entry("static", date, evidence, svcs[svc]) for svc in order_services(svcs)}


def order_services(svcs):
    return sorted(svcs, key=lambda s: (s != CORE, s))


def service_entry(level, date, evidence, names):
    e = {"level": level, "date": date, "evidence": evidence}
    for section in SECTIONS:
        e[section] = dict(sorted(names.get(section, {}).items()))
    if names.get("unresolved"):
        e["unresolved"] = dict(sorted(names["unresolved"].items()))
    return e


def today():
    return datetime.date.today().isoformat()


def write(path, rec):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(rec, indent=2) + "\n")


def fresh(side, exe, steam_build=None, svn=None, verified=False, note="", symbols_dir=SYMBOLS, date=None):
    """A new format-2 record: every declared name at static (or unresolved)."""
    raw = Path(exe).read_bytes()
    sha = hashlib.sha256(raw).hexdigest()
    cv, pdb = codeview(raw)
    owner = declared(side, symbols_dir)
    syms, consts, feats, doc = resolve(side, exe, symbols_dir)
    f, s, c = flat_maps(syms, consts, feats)
    rec = {"sha256": sha, "side": side, "record": RECORD, "codeview": cv, "pdb": pdb}
    if steam_build:
        rec["steam_build"] = steam_build
    if svn:
        rec["svn"] = svn
    rec["verified"] = bool(verified)
    rec["note"] = note or ("offline resolve only (tools/sigport/build_record.py); not verified at runtime"
                           if not verified else "")
    rec["declared"] = declared_counts(owner)
    rec["features"], rec["symbols"], rec["constants"] = f, s, c
    rec["services"] = services_static(owner, offline_state(owner, syms, consts, feats, doc), date or today(),
                                      "resolved offline (tools/sigport/build_record.py)")
    return rec


def summary(rec):
    lv = {}
    for e in rec.get("services", {}).values():
        for section in SECTIONS:
            for v in e.get(section, {}).values():
                lv[v] = lv.get(v, 0) + 1
    d = rec.get("declared", {})
    off = [k for k, v in rec["features"].items() if not v]
    return (f"{len(rec['symbols'])} named symbols, {len(rec['constants'])} constants, "
            f"{len(rec['features']) - len(off)}/{len(rec['features'])} features on (off: {', '.join(off) or '-'}); "
            f"declared {d.get('symbols', 0)} symbols, {d.get('constants', 0)} constants, {d.get('features', 0)} "
            f"features; levels " + ", ".join(f"{k} {lv[k]}" for k in NAME_LEVELS if k in lv) +
            f"; verified={rec['verified']}")


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("side", choices=["server", "client"])
    ap.add_argument("exe")
    ap.add_argument("--steam-build", type=int)
    ap.add_argument("--svn", type=int)
    ap.add_argument("--verified", action="store_true")
    ap.add_argument("--note", default="")
    ap.add_argument("--out", help="default: sigdb/builds/<side>/<sha256>.json")
    ap.add_argument("--symbols", default=str(SYMBOLS), help=argparse.SUPPRESS)
    a = ap.parse_args(argv)
    rec = fresh(a.side, a.exe, a.steam_build, a.svn, a.verified, a.note, a.symbols)
    out = Path(a.out) if a.out else BUILDS / a.side / f"{rec['sha256']}.json"
    write(out, rec)
    print(f"{out}: {summary(rec)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
