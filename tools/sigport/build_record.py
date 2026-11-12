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
import re
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
    return declared_split(side, symbols_dir)[0]


def declared_split(side, symbols_dir=SYMBOLS):
    """(owner, captured): owner as declared(); captured = {section: names declared only by a pattern hole}."""
    files = definition_files(side, symbols_dir)
    owner = {s: {} for s in SECTIONS}
    for svc, doc in files:
        for section in SECTIONS:
            for name in doc.get(section, {}):
                owner[section].setdefault(name, svc)
    captured = {s: set() for s in SECTIONS}
    for svc, doc in files:
        for defn in doc.get("symbols", {}).values():
            for f in defn.get("find", []):
                for m in HOLE.finditer(f.get("pattern", "")):
                    section = "symbols" if m.group(2) == "rel32" else "constants"
                    if m.group(1) not in owner[section]:
                        owner[section][m.group(1)] = svc
                        captured[section].add(m.group(1))
    return owner, captured


def declared_counts(owner):
    return {s: len(owner[s]) for s in SECTIONS}


def count_text(owner, captured, section, listed=None):
    """'447 symbols (355 defined, 92 captured)', or with listed: '447 of 447 symbols listed (...)'."""
    n, c = len(owner[section]), len(captured[section])
    head = f"{n} {section}" if listed is None else f"{listed} of {n} {section} listed"
    return head + (f" ({n - c} defined, {c} captured)" if c else "")


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




# ---- refresh ----

def _norm(v):
    """A hex string of the record or the core's report -> int (the core prints negatives as 64-bit unsigned)."""
    return int(v, 16) & 0xFFFFFFFFFFFFFFFF


def name_levels(rec):
    """{(section, name): (service, level)} of a format-2 record ({} for format 1)."""
    out = {}
    for svc, e in rec.get("services", {}).items():
        for section in SECTIONS:
            for name, level in e.get(section, {}).items():
                out.setdefault((section, name), (svc, level))
    return out


def same_value(old, new, section, name):
    """Does the name hold the same offline result in both records? Named symbols and constants compare their values,
    probes and features compare that they resolved (the level is not unresolved)."""
    if section == "features":
        return bool(old["features"].get(name)) and bool(new["features"].get(name))
    if section == "symbols" and name.startswith("@"):
        return True  # the caller has checked that it still resolves
    a, b = old.get(section, {}).get(name), new.get(section, {}).get(name)
    return a is not None and b is not None and _norm(a) == _norm(b)


def refresh(old, side, exe, symbols_dir=SYMBOLS, demote_changed=False, date=None):
    """(record, report): `old` updated from the exe and today's sigdb. Levels move only where the facts moved: a new
    name enters at static (or unresolved), a dropped name leaves; verified, note, every service's level, date and
    evidence stay. A name at lab or seen whose service, value or resolve changed keeps nothing it proved: without
    demote_changed the refresh refuses (report["refused"]), with it the name drops to static (or unresolved)."""
    new = fresh(side, exe, symbols_dir=symbols_dir, date=date)
    if old.get("sha256") != new["sha256"]:
        raise SystemExit(f"refresh: the record is for {old.get('sha256')}, the exe is {new['sha256']}")
    if old.get("side", side) != side:
        raise SystemExit(f"refresh: the record is a {old.get('side')} record, not {side}")
    rec = {}
    for k in ("sha256", "side", "record", "codeview", "pdb"):
        rec[k] = new[k]
    for k in ("steam_build", "svn"):
        if k in old:
            rec[k] = old[k]
    rec["verified"] = old.get("verified", False)
    rec["note"] = old.get("note", "")
    for k in ("declared", "features", "symbols", "constants"):
        rec[k] = new[k]
    report = {"added": [], "dropped": [], "changed": [], "services_added": [], "services_dropped": [],
              "refused": False, "format1": "record" not in old}
    before = name_levels(old)
    after = name_levels(new)
    names = {}
    for (section, name), (svc, level) in after.items():
        if (section, name) not in before:
            if not report["format1"]:
                report["added"].append(f"{svc}: {section[:-1]} {name} ({level})")
            names[(section, name)] = level
            continue
        osvc, olevel = before[(section, name)]
        if olevel in ("lab", "seen"):
            if osvc == svc and level != "unresolved" and same_value(old, new, section, name):
                level = olevel
            else:
                why = (f"was {olevel} under {osvc}, now declared by {svc}" if osvc != svc else
                       f"was {olevel}, now {level}" if level == "unresolved" else
                       f"was {olevel} at {old.get(section, {}).get(name, 'resolved')}, now "
                       f"{new.get(section, {}).get(name, 'resolved')}")
                report["changed"].append(f"{svc}: {section[:-1]} {name}: {why}")
        names[(section, name)] = level
    for (section, name), (svc, level) in before.items():
        if (section, name) not in after:
            report["dropped"].append(f"{svc}: {section[:-1]} {name} ({level})")
    if report["format1"]:
        for section in SECTIONS:
            for name in old.get(section, {}):
                if (section, name) not in after:
                    report["dropped"].append(f"{section[:-1]} {name}")
    if report["changed"] and not demote_changed:
        report["refused"] = True
    services = {}
    for svc, e in new["services"].items():
        o = old.get("services", {}).get(svc)
        if o is None:
            report["services_added"].append(svc)
            head = {"level": e["level"], "date": e["date"], "evidence": e["evidence"]}
        else:
            head = {"level": o["level"], "date": o["date"], "evidence": o["evidence"]}
        lv = {s: {n: names[(s, n)] for n in e.get(s, {})} for s in SECTIONS}
        if e.get("unresolved"):
            lv["unresolved"] = e["unresolved"]
        services[svc] = service_entry(head["level"], head["date"], head["evidence"], lv)
    for svc in old.get("services", {}):
        if svc not in services:
            report["services_dropped"].append(svc)
    rec["services"] = services
    return rec, report


# ---- lab results ----

# How a boot's kiln.log shows that the service of a sigdb file loaded. Default: "service kiln.<file> vN published".
LOADED = {
    CORE: [r"\] boot complete"],
    "emf": [r"\] plugin kiln-emf\.dll loaded"],
    "quests": [r"\] service kiln\.knowledge v\d+ published", r"\] service kiln\.quests v\d+ published"],
}
SUMMARY = [re.compile(r"(\d+) PASS, (\d+) FAIL"), re.compile(r"(\d+) passed, (\d+) failed")]


def loaded_line(log, svc):
    for rx in LOADED.get(svc, [rf"\] service kiln\.{re.escape(svc)} v\d+ published"]):
        m = re.search(r"^.*" + rx + r".*$", log, re.M)
        if m:
            return m.group(0).strip()
    return ""


def test_result(kiln_dir, log, mod):
    """{passed, failed, skipped, finished, fails} of one test mod: logs/tests/<mod>.json (Kiln.Testing), else the
    last summary line the mod wrote to kiln.log ("N PASS, M FAIL" or "N passed, M failed")."""
    j = Path(kiln_dir) / "logs" / "tests" / f"{mod}.json"
    if j.is_file():
        d = json.loads(j.read_text(encoding="utf-8-sig"))
        fails = [f"{r.get('name')}: {r.get('outcome')}: {r.get('message', '')}".strip()
                 for r in d.get("results", []) if r.get("outcome") in ("fail", "timeout")]
        return {"passed": d.get("passed", 0), "failed": d.get("failed", 0), "skipped": d.get("skipped", 0),
                "finished": True, "source": f"logs/tests/{mod}.json", "fails": fails[:20]}
    mine = [ln for ln in log.splitlines() if f"[mod:{mod}]" in ln]
    last = None
    for ln in mine:
        for rx in SUMMARY:
            m = rx.search(ln)
            if m:
                last = (int(m.group(1)), int(m.group(2)), ln.strip())
    fails = [ln.split(f"[mod:{mod}]", 1)[1].strip() for ln in mine if re.search(r"\bFAIL\b", ln)
             and not any(rx.search(ln) for rx in SUMMARY)]
    errors = [ln.strip() for ln in log.splitlines() if f"[mod:{mod}]" in ln and ln.startswith("[E")
              and "FAIL" not in ln]
    if last is None:
        return {"passed": 0, "failed": 0, "skipped": 0, "finished": False, "source": "kiln.log",
                "fails": (fails + errors)[:20] or ["no summary line in kiln.log"]}
    return {"passed": last[0], "failed": last[1], "skipped": 0, "finished": True, "source": "kiln.log",
            "summary": last[2], "fails": (fails + errors)[:20]}


def collect(server_dir, boot, services, lab="", date=None):
    """A boot's result file (dict) from a stopped server folder: what the core resolved (logs/sigdb-report.json), which
    services loaded (kiln.log) and how each service's test mods ended. services: {sigdb file stem: [test mod ids]}."""
    k = Path(server_dir) / "kiln"
    rep = json.loads((k / "logs" / "sigdb-report.json").read_text())
    log = (k / "logs" / "kiln.log").read_text(encoding="utf-8", errors="replace").replace("\r", "")
    cfg = {}
    if (k / "kiln.json").is_file():
        cfg = json.loads((k / "kiln.json").read_text())
    side = "client" if "enshrouded.pdb" in log and "enshrouded_server.pdb" not in log else "server"
    m = re.search(r"side: (server|client)", log)
    if m:
        side = m.group(1)
    values = {"symbols": {}, "constants": {}, "features": {}}
    for n, r in rep.get("symbols", {}).items():
        values["symbols"][n] = (r.get("rva") or True) if r.get("ok") else False
    for n, r in rep.get("constants", {}).items():
        values["constants"][n] = r.get("value") if r.get("ok") else False
    for n, ok in rep.get("features", {}).items():
        values["features"][n] = bool(ok)
    errs = [ln.strip() for ln in log.splitlines() if ln.startswith("[E") and ("[kiln]" in ln or "[service:" in ln)]
    out = {"result": 1, "kind": "lab", "side": side, "sha256": rep.get("exe_sha256", ""), "kiln": rep.get("kiln", ""),
           "date": date or today(), "boot": boot, "lab": lab, "policy": cfg.get("policy", ""),
           "boot_complete": bool(re.search(r"\] boot complete", log)), "core_errors": errs[:20],
           "values": values, "services": {}}
    for svc, mods in services.items():
        line = loaded_line(log, svc)
        out["services"][svc] = {"loaded": bool(line), "loaded_line": line,
                                "tests": {mod: test_result(k, log, mod) for mod in mods}}
    return out


def carry(old_rec, date, evidence, level="lab"):
    """A result file that carries the levels an earlier record of this exe proved (its format-1 maps, e.g. from git
    history) to the names that still resolve to the same values."""
    return {"result": 1, "kind": "carry", "side": old_rec.get("side", ""), "sha256": old_rec["sha256"], "date": date,
            "level": level, "evidence": evidence,
            "values": {s: dict(old_rec.get(s, {})) for s in SECTIONS}}


def _core_agrees(rec, res, svc):
    """[mismatches] between the offline resolve in the record and what the running core resolved, for one service."""
    bad = []
    e = rec["services"][svc]
    vals = res["values"]
    for section in SECTIONS:
        for name, level in e.get(section, {}).items():
            ok = level != "unresolved"
            core = vals.get(section, {}).get(name)
            if core is None:
                bad.append(f"{section[:-1]} {name}: not in the core's report")
            elif section == "features" or (section == "symbols" and name.startswith("@")):
                if bool(core) != ok:
                    bad.append(f"{section[:-1]} {name}: core {'on' if core else 'off'}, offline "
                               f"{'resolved' if ok else 'unresolved'}")
            elif not ok:
                if core is not False:
                    bad.append(f"{section[:-1]} {name}: core resolved {core}, offline unresolved")
            elif core is False:
                bad.append(f"{section[:-1]} {name}: core unresolved, offline {rec[section].get(name)}")
            elif isinstance(core, str) and _norm(core) != _norm(rec[section][name]):
                bad.append(f"{section[:-1]} {name}: core {core}, offline {rec[section][name]}")
    return bad


def _raise(e, level, date, evidence, only=None):
    """Raise a service and its resolved names to `level` (never lowers: seen stays seen)."""
    rank = {lv: i for i, lv in enumerate(LEVELS)}
    n = 0
    for section in SECTIONS:
        for name, cur in e.get(section, {}).items():
            if cur == "unresolved" or (only is not None and (section, name) not in only):
                continue
            if rank[cur] < rank[level]:
                e[section][name] = level
                n += 1
    if rank[e["level"]] <= rank[level]:
        e["level"], e["date"], e["evidence"] = level, date, evidence
    return n


def lab_evidence(results, svc):
    parts = []
    for r in results:
        s = r["services"][svc]
        tests = ", ".join(f"{m} {t['passed']}/{t['failed']}" for m, t in s["tests"].items())
        parts.append(f"{tests} (boot {r['boot']})")
    labs = sorted({r["lab"] for r in results if r.get("lab")})
    pol = sorted({r["policy"] for r in results if r.get("policy")})
    n = len(results)
    return (f"lab {', '.join(labs) or '?'}, policy {', '.join(pol) or '?'}, {n} boot{'s' if n > 1 else ''}, "
            f"Kiln {results[-1].get('kiln', '?')}: " + "; ".join(parts) +
            " (passed/failed); the core resolved the same values as the offline resolve")


def apply(rec, results):
    """Record lab results into a format-2 record. -> (applied lines, failure lines). A service rises to lab only when
    every result that names it shows it loaded, every listed test mod finished with no failure, and the core resolved
    the same value for each of its names; else nothing about it changes."""
    if rec.get("record") != RECORD:
        raise SystemExit("apply: the record is format 1; run --refresh first")
    done, failed = [], []
    by_svc = {}
    for res in results:
        if res.get("result") != 1:
            raise SystemExit("apply: not a result file (result: 1)")
        if res.get("sha256") != rec["sha256"]:
            raise SystemExit(f"apply: result for {res.get('sha256')}, record for {rec['sha256']}")
        if res.get("kind") == "carry":
            only = set()
            for section in SECTIONS:
                for name, v in res["values"].get(section, {}).items():
                    if section == "features":
                        if v and rec["features"].get(name):
                            only.add((section, name))
                    elif name in rec.get(section, {}) and _norm(v) == _norm(rec[section][name]):
                        only.add((section, name))
            for svc, e in rec["services"].items():
                mine = {(s, n) for s in SECTIONS for n in e.get(s, {})} & only
                if mine:
                    n = _raise(e, res["level"], res["date"], res["evidence"], only=mine)
                    done.append(f"{svc}: {n} name(s) to {res['level']} (carried, {len(mine)} with the same value)")
            continue
        for svc in res.get("services", {}):
            by_svc.setdefault(svc, []).append(res)
    for svc, rs in by_svc.items():
        if svc not in rec["services"]:
            failed.append(f"{svc}: no such service in the record")
            continue
        why = []
        for r in rs:
            s = r["services"][svc]
            if not r.get("boot_complete"):
                why.append(f"boot {r['boot']}: boot did not complete")
            if not s.get("loaded"):
                why.append(f"boot {r['boot']}: the service did not load")
            if not s.get("tests"):
                why.append(f"boot {r['boot']}: no test mod")
            for mod, t in s.get("tests", {}).items():
                if not t.get("finished"):
                    why.append(f"boot {r['boot']}: {mod} did not finish: " + "; ".join(t.get("fails", [])[:3]))
                elif t.get("failed"):
                    why.append(f"boot {r['boot']}: {mod} {t['passed']}/{t['failed']}: " + "; ".join(t.get("fails", [])[:5]))
            for b in _core_agrees(rec, r, svc):
                why.append(f"boot {r['boot']}: {b}")
        if why:
            failed.append(f"{svc}: stays {rec['services'][svc]['level']}: " + " | ".join(why))
            continue
        e = rec["services"][svc]
        n = _raise(e, "lab", max(r["date"] for r in rs), lab_evidence(rs, svc))
        done.append(f"{svc}: lab, {n} name(s) raised ({e['evidence']})")
    return done, failed


# ---- check ----

def newest(records):
    """The record of the newest build: highest steam_build (a record without one counts as oldest)."""
    return max(records, key=lambda r: (r[1].get("steam_build", 0), r[0].name))


def check(sides=("server", "client"), symbols_dir=SYMBOLS, builds_dir=BUILDS):
    """-> (ok, lines). The newest record of each side must list every declared name once, each with a valid level."""
    ok, lines = True, []
    for side in sides:
        files = sorted((Path(builds_dir) / side).glob("*.json"))
        if not files:
            lines.append(f"FAIL {side}: no build record in {Path(builds_dir) / side}")
            ok = False
            continue
        recs = [(p, json.loads(p.read_text())) for p in files]
        top_path, top = newest(recs)
        for p, r in recs:
            if p != top_path:
                lines.append(f"     {side} {p.stem[:8]}… (steam build {r.get('steam_build', '?')}): superseded by "
                             f"{top_path.stem[:8]}…, not checked")
        tag = f"{side} {top_path.stem[:8]}… (steam build {top.get('steam_build', '?')})"
        bad = []
        if top.get("record") != RECORD:
            bad.append(f"record format {top.get('record', 1)}, not {RECORD}: run build_record.py {side} <exe> --refresh")
        owner, captured = declared_split(side, symbols_dir)
        listed = {s: {} for s in SECTIONS}
        levels = {}
        for svc, e in top.get("services", {}).items():
            if e.get("level") not in LEVELS:
                bad.append(f"{svc}: service level {e.get('level')!r} is not one of {', '.join(LEVELS)}")
            if not re.fullmatch(r"\d{4}-\d\d-\d\d", str(e.get("date", ""))):
                bad.append(f"{svc}: date {e.get('date')!r} is not YYYY-MM-DD")
            if not str(e.get("evidence", "")).strip():
                bad.append(f"{svc}: no evidence line")
            unres = e.get("unresolved", {})
            for section in SECTIONS:
                for name, level in e.get(section, {}).items():
                    if level not in NAME_LEVELS:
                        bad.append(f"{svc}: {section[:-1]} {name}: level {level!r} is not one of {', '.join(NAME_LEVELS)}")
                    levels[level] = levels.get(level, 0) + 1
                    if name in listed[section]:
                        bad.append(f"{section[:-1]} {name} is listed twice ({listed[section][name]}, {svc})")
                    listed[section].setdefault(name, svc)
                    if (level == "unresolved") != (name in unres):
                        bad.append(f"{svc}: {section[:-1]} {name}: " + ("unresolved without a reason"
                                   if level == "unresolved" else "has an unresolved reason but level " + level))
        for section in SECTIONS:
            for name, svc in owner[section].items():
                if name not in listed[section]:
                    bad.append(f"missing: {section[:-1]} {name} (declared by {svc})")
                elif listed[section][name] != svc:
                    bad.append(f"{section[:-1]} {name} is listed under {listed[section][name]}, declared by {svc}")
            for name, svc in listed[section].items():
                if name not in owner[section]:
                    bad.append(f"{section[:-1]} {name} is listed under {svc} but sigdb no longer declares it")
        counts = "; ".join(count_text(owner, captured, s, sum(1 for n in owner[s] if n in listed[s])) for s in SECTIONS)
        lv = ", ".join(f"{k} {levels[k]}" for k in NAME_LEVELS if k in levels)
        if bad:
            ok = False
            lines.append(f"FAIL {tag}: {counts}; levels {lv or '-'}")
            lines += [f"  {b}" for b in bad]
            lines.append(f"  fix: python3 tools/sigport/build_record.py {side} <exe> --refresh")
        else:
            lines.append(f"ok   {tag}: {counts}; each once, levels {lv}")
    return ok, lines


# ---- command line ----

def summary(rec, symbols_dir=SYMBOLS):
    lv = {}
    for e in rec.get("services", {}).values():
        for section in SECTIONS:
            for v in e.get(section, {}).values():
                lv[v] = lv.get(v, 0) + 1
    owner, captured = declared_split(rec["side"], symbols_dir)
    off = [k for k, v in rec["features"].items() if not v]
    return (f"{len(rec['symbols'])} named symbols, {len(rec['constants'])} constants, "
            f"{len(rec['features']) - len(off)}/{len(rec['features'])} features on (off: {', '.join(off) or '-'}); "
            "declared " + ", ".join(count_text(owner, captured, s) for s in SECTIONS) +
            "; levels " + ", ".join(f"{k} {lv[k]}" for k in NAME_LEVELS if k in lv) + f"; verified={rec['verified']}")


def record_path(side, sha, builds_dir=BUILDS):
    return Path(builds_dir) / side / f"{sha}.json"


def main_check(argv):
    ap = argparse.ArgumentParser(prog="build_record.py check",
                                 description="the newest record of each side lists every declared name, once, with a level")
    ap.add_argument("sides", nargs="*", choices=["server", "client"], default=None)
    ap.add_argument("--symbols", default=str(SYMBOLS), help=argparse.SUPPRESS)
    ap.add_argument("--builds", default=str(BUILDS), help=argparse.SUPPRESS)
    a = ap.parse_args(argv)
    ok, lines = check(a.sides or ("server", "client"), a.symbols, a.builds)
    print("\n".join(lines))
    return 0 if ok else 1


def main_apply(argv):
    ap = argparse.ArgumentParser(prog="build_record.py apply",
                                 description="record lab results (files from collect or carry) into the build record")
    ap.add_argument("results", nargs="+")
    ap.add_argument("--record", help="default: sigdb/builds/<side>/<sha256>.json of the results")
    ap.add_argument("--builds", default=str(BUILDS), help=argparse.SUPPRESS)
    a = ap.parse_args(argv)
    results = [json.loads(Path(p).read_text()) for p in a.results]
    path = Path(a.record) if a.record else record_path(results[0]["side"], results[0]["sha256"], a.builds)
    rec = json.loads(path.read_text())
    done, failed = apply(rec, results)
    write(path, rec)
    for ln in done:
        print(f"  ok  {ln}")
    for ln in failed:
        print(f"FAIL  {ln}")
    print(f"{path}: {len(done)} applied, {len(failed)} not")
    return 1 if failed else 0


def main_collect(argv):
    ap = argparse.ArgumentParser(prog="build_record.py collect",
                                 description="write the result file of one lab boot from the stopped server folder")
    ap.add_argument("server_dir")
    ap.add_argument("--boot", required=True, help="a name for this boot")
    ap.add_argument("--service", action="append", default=[], metavar="FILE=MOD[,MOD]",
                    help="a sigdb file stem (core for <side>.json) and its test mods; repeat per service")
    ap.add_argument("--lab", default="")
    ap.add_argument("-o", "--out", required=True)
    a = ap.parse_args(argv)
    services = {}
    for s in a.service:
        svc, _, mods = s.partition("=")
        services[svc] = [m for m in mods.split(",") if m]
    res = collect(a.server_dir, a.boot, services, a.lab)
    write(a.out, res)
    for svc, s in res["services"].items():
        tests = ", ".join(f"{m} {t['passed']}/{t['failed']}{'' if t['finished'] else ' (no summary)'}"
                          for m, t in s["tests"].items())
        print(f"{svc:12s} {'loaded' if s['loaded'] else 'NOT LOADED'}  {tests}")
    print(f"{a.out}: boot {a.boot}, sha {res['sha256'][:8]}…, boot complete {res['boot_complete']}, "
          f"{len(res['core_errors'])} core/service error line(s)")
    return 0


def main_carry(argv):
    ap = argparse.ArgumentParser(prog="build_record.py carry",
                                 description="a result file from an earlier record of the same exe (its format-1 maps)")
    ap.add_argument("old_record")
    ap.add_argument("--date", required=True)
    ap.add_argument("--evidence", required=True)
    ap.add_argument("--level", default="lab", choices=["lab", "seen"])
    ap.add_argument("-o", "--out", required=True)
    a = ap.parse_args(argv)
    write(a.out, carry(json.loads(Path(a.old_record).read_text()), a.date, a.evidence, a.level))
    print(f"{a.out}: carry {a.level} from {a.old_record}")
    return 0


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    sub = {"check": main_check, "apply": main_apply, "collect": main_collect, "carry": main_carry}
    if argv and argv[0] in sub:
        return sub[argv[0]](argv[1:])
    ap = argparse.ArgumentParser(
        epilog="other commands: check [side...], collect <server dir> ..., apply <result.json>..., carry <old record> ...")
    ap.add_argument("side", choices=["server", "client"])
    ap.add_argument("exe")
    ap.add_argument("--steam-build", type=int)
    ap.add_argument("--svn", type=int)
    ap.add_argument("--verified", action="store_true")
    ap.add_argument("--note", default="", help="with --refresh: replaces the note")
    ap.add_argument("--refresh", action="store_true",
                    help="update the existing record: new names at static, dropped names out, levels, verified, note "
                         "and evidence kept")
    ap.add_argument("--demote-changed", action="store_true",
                    help="with --refresh: a lab/seen name whose value changed drops to static instead of refusing")
    ap.add_argument("--out", help="default: sigdb/builds/<side>/<sha256>.json")
    ap.add_argument("--symbols", default=str(SYMBOLS), help=argparse.SUPPRESS)
    ap.add_argument("--builds", default=str(BUILDS), help=argparse.SUPPRESS)
    a = ap.parse_args(argv)
    sha = hashlib.sha256(Path(a.exe).read_bytes()).hexdigest()
    out = Path(a.out) if a.out else record_path(a.side, sha, a.builds)
    if a.refresh:
        if a.verified:
            ap.error("--refresh never changes verified")
        if not out.is_file():
            ap.error(f"--refresh: no record {out}")
        old = json.loads(out.read_text())
        rec, rep = refresh(old, a.side, a.exe, a.symbols, a.demote_changed)
        for k in ("steam_build", "svn"):
            if getattr(a, k) and k not in rec:
                rec[k] = getattr(a, k)
        if a.note:
            rec["note"] = a.note
        for ln in rep["changed"]:
            print(f"changed  {ln}")
        if rep["refused"]:
            print(f"{out}: not written: {len(rep['changed'])} name(s) at lab or seen changed; run the lab sequence "
                  "for their services, or --demote-changed to put them back to static")
            return 1
        if rep["format1"]:
            print(f"format 1 record: every name enters at static (or unresolved)")
        for ln in rep["added"]:
            print(f"added    {ln}")
        for ln in rep["dropped"]:
            print(f"dropped  {ln}")
        for svc in rep["services_added"]:
            print(f"added    service {svc}")
        for svc in rep["services_dropped"]:
            print(f"dropped  service {svc}")
        text = json.dumps(rec, indent=2) + "\n"
        if out.read_text() == text:
            print(f"{out}: unchanged; {summary(rec, a.symbols)}")
            return 0
        out.write_text(text)
        print(f"{out}: refreshed; {summary(rec, a.symbols)}")
        return 0
    if out.is_file() and any(lv in ("lab", "seen") for _s, lv in name_levels(json.loads(out.read_text())).values()):
        ap.error(f"{out} holds lab or seen levels: use --refresh (a new record would drop them)")
    rec = fresh(a.side, a.exe, a.steam_build, a.svn, a.verified, a.note, a.symbols)
    write(out, rec)
    print(f"{out}: {summary(rec, a.symbols)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
