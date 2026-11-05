#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""sigport benchmarks: precision/recall of `port.py` against hand-verified definitions.

    python3 tools/sigport/bench.py compare <report.json> --truth-defs <side>.json --truth-exe <exe> [--json out]
    python3 tools/sigport/bench.py replay --old <1273043 exe> --new <1285601 exe> [--out DIR]
        1. derive a sigdb for the old exe only: port today's sigdb backwards (new -> old) with --regenerate-all
           --no-generalize, so every pattern is cut from the old exe's bytes;
        2. hand-check it: today's sigdb resolves on the old exe too (its patterns were generalised by hand), and EMF
           0.3.3 documents server_1273043 RVAs for its signatures;
        3. blind: port the derived sigdb old -> new (the tool sees only the old exe, the derived sigdb, the new exe),
           with and without exact pattern hits;
        4. compare with today's sigdb resolved on the new exe (hand-verified, lab-verified build record).
    python3 tools/sigport/bench.py s2c --server <server exe> --client <client exe> [--out DIR]
        server.json (+ server.d) ported from the server exe to the client exe of the same build; truth: client.json
        (+ the composed client.d, hand-ported overlays) resolved on the client exe.
    python3 tools/sigport/bench.py emf --old <1273043 exe> --new <1285601 exe> [--out DIR]
        EMF 0.3.3's 42 server signatures (plain byte patterns, no holes, cut for 1273043; 13 of them break on
        1285601) as a sigdb; ported old -> new; truth: the RVAs in sigdb/evidence/emf-0.3.3-signatures.json (automatic
        and hand ports).
"""
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import port  # noqa: E402
from peimg import Image  # noqa: E402
from sides import merged  # noqa: E402
from sigdb_check import Resolver  # noqa: E402

REPO = Path(__file__).resolve().parents[2]


def truth_of(defs, exe):
    syms, consts, _ = Resolver(Image(exe), merged(defs)).run()
    return ({n: r["rva"] for n, r in syms.items() if not n.startswith("@") and r.get("ok")},
            {n: r["value"] for n, r in consts.items() if r.get("ok")})


def score(report, tsyms, tconsts, thr=None):
    thr = report["options"]["min_confidence"] if thr is None else thr
    out = {"symbols": {}, "constants": {}}
    for kind, truth, got in (("symbols", tsyms, report["symbols"]), ("constants", tconsts, report["constants"])):
        rows = {}
        # only what the input definitions name (a target-only symbol cannot be ported)
        truth = {n: v for n, v in truth.items() if n in got}
        names = set(truth) | {n for n, r in got.items() if not n.startswith("@") and r.get("ok_new")}
        for n in sorted(names):
            if n.startswith("@"):
                continue
            g = got.get(n, {})
            val = int(g["new"], 16) if g.get("new") else None
            reported = bool(g.get("ok_new")) and g.get("confidence", 0) >= thr and val is not None
            t = truth.get(n)
            if reported and t is not None and val == t:
                st = "TP"
            elif reported:
                st = "FP"
            elif t is not None:
                st = "FN"
            else:
                continue
            rows[n] = {"status": st, "truth": hex(t) if t is not None else None, "port": g.get("new"),
                       "confidence": g.get("confidence"), "method": g.get("method"),
                       "old": g.get("old")}
        tp = sum(1 for r in rows.values() if r["status"] == "TP")
        fp = sum(1 for r in rows.values() if r["status"] == "FP")
        fn = sum(1 for r in rows.values() if r["status"] == "FN")
        out[kind] = {"tp": tp, "fp": fp, "fn": fn,
                     "precision": round(tp / (tp + fp), 4) if tp + fp else None,
                     "recall": round(tp / (tp + fn), 4) if tp + fn else None, "rows": rows}
    return out


def print_score(title, sc, verbose=True):
    print(f"== {title}")
    for kind in ("symbols", "constants"):
        k = sc[kind]
        print(f"   {kind:9s} precision {k['precision']}  recall {k['recall']}  (TP {k['tp']}, FP {k['fp']}, FN {k['fn']})")
        if verbose:
            for n, r in k["rows"].items():
                if r["status"] != "TP":
                    print(f"      {r['status']} {n}: truth {r['truth']} port {r['port']} conf {r['confidence']} {r['method']}")


def run_port(args):
    print("+ port.py " + " ".join(args), file=sys.stderr)
    rc = port.main(args + ["--quiet"])
    return rc


def cmd_compare(a):
    rep = json.loads(Path(a.report).read_text())
    ts, tc = truth_of(a.truth_defs, a.truth_exe)
    sc = score(rep, ts, tc)
    print_score(a.report, sc)
    if a.json:
        Path(a.json).write_text(json.dumps(sc, indent=2) + "\n")
    return 0


def handcheck_old(back_report, old_exe, defs):
    """The derived old-build sigdb vs (a) today's sigdb on the old exe, (b) EMF's documented server_1273043 RVAs."""
    ts, tc = truth_of(defs, old_exe)
    sc = score(back_report, ts, tc)
    emf = json.loads((REPO / "sigdb/evidence/emf-0.3.3-signatures.json").read_text())
    by_new = {}
    for r in emf:
        if not r["server_1273043"]:
            continue
        new = (r["new"] or r["new_rebased"] or [None])[0]
        by_new.setdefault(new, r)
    hp = json.loads((REPO / "sigdb/evidence/server-26404334-hand-ports.json").read_text())
    for p in hp["ports"]:
        r = next(x for x in emf if x["name"] == p["name"])
        by_new[p["rva"]] = r
    emf_rows = []
    for n, x in back_report["symbols"].items():
        if n.startswith("@") or not x.get("old"):
            continue
        r = by_new.get(x["old"])
        if r is None:
            continue
        doc = r["server_1273043"]
        emf_rows.append({"symbol": n, "emf": r["name"], "new_build": x["old"], "emf_1273043": hex(doc),
                         "port_1273043": x["new"], "agree": x["new"] == hex(doc)})
    return sc, emf_rows


def cmd_replay(a):
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    defs = str(REPO / "sigdb/symbols/server.json")
    back = out / "back"
    run_port(["--old", a.new, "--new", a.old, "--defs", defs, "--out", str(back), "--regenerate-all",
              "--no-generalize", "--cache", a.cache])
    brep = json.loads((back / "report.json").read_text())
    sc_b, emf_rows = handcheck_old(brep, a.old, defs)
    print_score("hand check: derived 1273043 sigdb vs today's sigdb resolved on 1273043", sc_b)
    agree = sum(1 for r in emf_rows if r["agree"])
    print(f"== hand check: derived 1273043 sigdb vs EMF 0.3.3 server_1273043 RVAs: {agree}/{len(emf_rows)} agree")
    for r in emf_rows:
        if not r["agree"]:
            print(f"      DIFF {r['symbol']} ({r['emf']}): EMF {r['emf_1273043']} port {r['port_1273043']}")
    # the derived sigdb must resolve on the old exe by itself
    dts, dtc = truth_of(back / "tree/server.json", a.old)
    print(f"== derived sigdb on 1273043: {len(dts)} symbols, {len(dtc)} constants resolve")
    ts, tc = truth_of(defs, a.new)
    results = {"handcheck_today_sigdb_on_old": strip(sc_b), "handcheck_emf": emf_rows}
    for label, extra in (("blind", []), ("blind-no-exact", ["--no-exact"])):
        fwd = out / label
        run_port(["--old", a.old, "--new", a.new, "--defs", str(back / "tree/server.json"), "--out", str(fwd),
                  "--cache", a.cache] + extra)
        rep = json.loads((fwd / "report.json").read_text())
        sc = score(rep, ts, tc)
        print_score(f"{label}: derived 1273043 sigdb ported to 1285601 vs today's sigdb on 1285601", sc)
        s = rep["summary"]
        print(f"   strategies changed {len(s['strategies_changed'])}; features lost {s['features']['lost']}; "
              f"matcher {s['matcher']}")
        results[label] = strip(sc)
        results[label]["summary"] = {k: s[k] for k in ("symbols", "constants", "features", "matcher")}
        results[label]["methods"] = methods(rep)
    (out / "replay.json").write_text(json.dumps(results, indent=2) + "\n")
    print(out / "replay.json")
    return 0


def methods(rep):
    c = {}
    for n, x in rep["symbols"].items():
        for st in x["strategies"]:
            if st["method"]:
                c[st["method"]] = c.get(st["method"], 0) + 1
    return c


def strip(sc):
    """Keep only the non-TP rows (the TP list is long and in the per-run report)."""
    out = {}
    for k, v in sc.items():
        out[k] = {x: y for x, y in v.items() if x != "rows"}
        out[k]["misses"] = {n: r for n, r in v["rows"].items() if r["status"] != "TP"}
    return out


def cmd_s2c(a):
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    ts, tc = truth_of(REPO / "sigdb/symbols/client.json", a.client)
    results = {}
    for label, extra in (("s2c", []), ("s2c-no-exact", ["--no-exact"])):
        d = out / label
        run_port(["--old", a.server, "--new", a.client, "--defs", str(REPO / "sigdb/symbols/server.json"),
                  "--side", "server", "--out", str(d), "--cache", a.cache] + extra)
        rep = json.loads((d / "report.json").read_text())
        sc = score(rep, ts, tc)
        print_score(f"{label}: server sigdb ported server exe -> client exe vs client sigdb on the client", sc)
        results[label] = strip(sc)
        results[label]["methods"] = methods(rep)
    (out / "s2c.json").write_text(json.dumps(results, indent=2) + "\n")
    print(out / "s2c.json")
    return 0


def cmd_emf(a):
    out = Path(a.out)
    tree = out / "emf-tree"
    (tree / "server.d").mkdir(parents=True, exist_ok=True)
    emf = json.loads((REPO / "sigdb/evidence/emf-0.3.3-signatures.json").read_text())
    hp = {p["name"]: p for p in json.loads((REPO / "sigdb/evidence/server-26404334-hand-ports.json").read_text())["ports"]}
    old = Image(a.old)
    doc = {"format": 1, "side": "server", "comment": "EMF 0.3.3 server signatures (bench.py emf)", "symbols": {}}
    truth = {}
    for r in emf:
        if not r["server_1273043"]:
            continue
        name = "emf/" + r["name"]
        hits = old.find(r["pattern"], 3)
        if hits != [r["server_1273043"]]:
            continue  # EMF's pattern must resolve on 1273043 where EMF says
        doc["symbols"][name] = {"find": [{"pattern": r["pattern"]}]}
        new = r["new"] or r["new_rebased"]
        if r["name"] in hp:
            truth[name] = int(hp[r["name"]]["rva"], 16)
        elif new and len(new) == 1:
            truth[name] = int(new[0], 16)
    (tree / "server.json").write_text(json.dumps(doc, indent=2) + "\n")
    print(f"emf: {len(doc['symbols'])} EMF server signatures resolve on 1273043; truth on 1285601 for {len(truth)}")
    broken = [n for n, d in doc["symbols"].items() if len(Image(a.new).find(d["find"][0]["pattern"], 2)) != 1]
    print(f"emf: {len(broken)} of them do not resolve on 1285601 as written: {', '.join(broken)}")
    results = {"broken_on_new": broken}
    for label, extra in (("emf", []), ("emf-no-exact", ["--no-exact"])):
        d = out / label
        run_port(["--old", a.old, "--new", a.new, "--defs", str(tree / "server.json"), "--out", str(d),
                  "--cache", a.cache] + extra)
        rep = json.loads((d / "report.json").read_text())
        sc = score(rep, truth, {})
        print_score(f"{label}: EMF signatures ported 1273043 -> 1285601 vs documented ports", sc)
        brk = {n: sc["symbols"]["rows"].get(n, {}).get("status") for n in broken}
        print(f"   the {len(broken)} broken ones: {brk}")
        results[label] = strip(sc)
        results[label]["broken"] = brk
    (out / "emf.json").write_text(json.dumps(results, indent=2) + "\n")
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("compare")
    c.add_argument("report")
    c.add_argument("--truth-defs", required=True)
    c.add_argument("--truth-exe", required=True)
    c.add_argument("--json")
    for name in ("replay", "emf"):
        r = sub.add_parser(name)
        r.add_argument("--old", required=True)
        r.add_argument("--new", required=True)
        r.add_argument("--out", default=str(REPO / ".out" / "sigport" / f"bench-{name}"))
        r.add_argument("--cache", default=str(port.DEFAULT_CACHE))
    s = sub.add_parser("s2c")
    s.add_argument("--server", required=True)
    s.add_argument("--client", required=True)
    s.add_argument("--out", default=str(REPO / ".out" / "sigport" / "bench-s2c"))
    s.add_argument("--cache", default=str(port.DEFAULT_CACHE))
    a = ap.parse_args()
    return {"compare": cmd_compare, "replay": cmd_replay, "s2c": cmd_s2c, "emf": cmd_emf}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
