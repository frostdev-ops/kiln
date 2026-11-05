# SPDX-License-Identifier: MIT
"""Build tools/impact/data from evidence (the installed game, read only):

    natives.json   registerNative table (scan of the exe) + per-argument evidence from every vanilla call site
                   (types and pin names of the variables passed, how often unconnected, what the result feeds)
                   + the hand-written notes in data/natives-notes.json (semantics from Kiln's decompiles)
    types.json     the impact types of vanilla variables (impact name -> hash, size) + their reflection closure
    configs.json   the config vocabulary: every configId of a vanilla variable, its value-node names and type

`impact.py survey` writes them to $KILN_OUT/impact-data/ (or --out). Argument direction is inferred
("out" = a node-owned variable that later instructions read, "in" = connected to another node's value or a config;
evidence counts are kept so a reader can judge). docs/re/impact.md explains the method.
"""
import collections
import json
import re
from pathlib import Path

from . import ops, game as G
from .catalog import DATA
from .program import fnv1a32

PRIMS = ["bool", "uint8", "sint8", "uint16", "sint16", "uint32", "sint32", "uint64", "sint64", "float32", "float64",
         "float2", "float3", "float4", "Time"]

# config ids read by C++ (not program variables), from docs/re/abilities.md (actor_pay_usage_cost 0x45360, 0x40d00)
CPP_CONFIGS = [
    (0xAF236ECE, "ManaCost", "sint32"), (0x727D7F78, "StaminaCost", "sint32"),
    (0x2DE5F1F5, "SpecialAbilityCost", "sint32"), (0xC8492400, "FuelAmount", None),
    (0xE99AAFF8, "FuelCharges", None), (0x220E6B10, "UsedItem", None),
]


GENERIC_PINS = {"value", "valueId", "result"}


def _owned(dbg, nat):
    node = G.node_of(dbg).lower()
    return any(node.startswith(x.lower()) for x in {nat["node"], nat["name"]} if x)


def _args_of(ins, k, argc):
    """Push instructions for args[0..argc-1] of the ECALL at ins[k] (None where an arg was computed)."""
    out = []
    j = k - 1
    for _ in range(argc):
        if j < 0 or ins[j].code not in (11, 12, 13):
            out.append(None)
            j = -1
            continue
        out.append(ins[j])
        j -= 1
    return out


def survey(g, exe_for_natives=None, other_exe=None):
    reg = G.scan_natives(exe_for_natives or g.exe)
    regs = reg["sites"]
    other = G.scan_natives(other_exe) if other_exe else None
    natives = collections.OrderedDict()
    for r in regs:
        h = r["hash"]
        if h in natives:
            natives[h]["registrations"] += 1
            continue
        natives[h] = {"hash": f"0x{h:08x}", "name": r["name"], "node": None, "argc": r["argc"],
                      "fn": {"server" if "server" in str(exe_for_natives or g.exe) else "exe": f"0x{r['fn']:x}"},
                      "site": f"0x{r['site']:x}", "registrations": 1}
    if other:
        for r in other["sites"]:
            if r["hash"] in natives:
                natives[r["hash"]]["fn"]["client" if "server" not in str(other_exe) else "server"] = f"0x{r['fn']:x}"
    # node names: the hash is FNV-1a of the node name; try the display name capitalised, then exe strings
    want = {h for h in natives}
    found = {}
    for h, n in natives.items():
        for c in (n["name"], n["name"][:1].upper() + n["name"][1:]):
            if fnv1a32(c) == h:
                found[h] = c
    missing = want - set(found)
    if missing:
        b = Path(g.exe).read_bytes()
        for m in re.finditer(rb"[A-Za-z_][A-Za-z0-9_]{1,63}", b):
            s = m.group()
            h = fnv1a32(s)
            if h in missing and h not in found:
                found[h] = s.decode()
    for h, n in natives.items():
        n["node"] = found.get(h)

    names = G.impact_names(g.types, g.exe)
    tname = lambda h: names.get(h, (f"0x{h:08x}", None))[0]

    ev = {h: {"calls": 0, "programs": set(), "next": collections.Counter(), "sections": collections.Counter(),
              "args": [{"types": collections.Counter(), "own": collections.Counter(), "src": collections.Counter(),
                        "unconnected": 0, "const1": 0, "computed": 0, "cfg": 0, "out": 0, "in": 0}
                       for _ in range(n["argc"])]} for h, n in natives.items()}
    var_types = collections.Counter()
    cfg = collections.defaultdict(lambda: {"names": collections.Counter(), "types": collections.Counter(),
                                           "programs": set()})
    nprog = 0
    for guid, raw, p in g.programs():
        nprog += 1
        for v in p.variables:
            var_types[v.type] += 1
            if v.config:
                c = cfg[v.config]
                c["names"][G.node_of(v.dbg)] += 1
                c["types"][tname(v.type)] += 1
                c["programs"].add(guid)
        # every reference to each variable, in program order (code, then shutdown): (order, site or None, how)
        refs = collections.defaultdict(list)
        decoded = {}
        order = 0
        for sec, words in (("code", p.code), ("shutdown", p.shutdown)):
            ins = ops.decode(words)
            decoded[sec] = ins
            for k, i in enumerate(ins):
                order += 1
                if i.code == ops.ECALL:
                    n = natives.get(i.args[1])
                    if not n:
                        continue
                    for a in _args_of(ins, k, n["argc"]):
                        if a is not None and a.code == 11 and ops.is_handle(a.args[0]):
                            refs[a.args[0] & 0xFFFF].append((order, i.args[0], "arg"))
                elif i.code in (22, 24) and ops.is_handle(i.args[0]):
                    refs[i.args[0] & 0xFFFF].append((order, None, "gload" if i.code == 22 else "gstore"))
        order = 0
        for sec, ins in decoded.items():
            for k, i in enumerate(ins):
                order += 1
                if i.code != ops.ECALL or i.args[1] not in natives:
                    continue
                n = natives[i.args[1]]
                e = ev[i.args[1]]
                e["calls"] += 1
                e["programs"].add(guid)
                e["sections"][sec] += 1
                nxt = ins[k + 1] if k + 1 < len(ins) else None
                e["next"][nxt.op.name if nxt and nxt.op else "end"] += 1
                for ai, a in enumerate(_args_of(ins, k, n["argc"])):
                    s = e["args"][ai]
                    if a is None:
                        s["computed"] += 1
                    elif a.code == 12:
                        s["unconnected"] += 1
                    elif a.code == 13:
                        s["const1"] += 1
                    elif ops.is_handle(a.args[0]) and (a.args[0] & 0xFFFF) < len(p.variables):
                        idx = a.args[0] & 0xFFFF
                        v = p.variables[idx]
                        s["types"][tname(v.type)] += 1
                        r = refs[idx]
                        others = [x for x in r if x[1] != i.args[0]]
                        here_first = r and r[0][1] == i.args[0]
                        nonzero = any(p.data[v.offset:v.offset + v.size])
                        pin = G.pin_of(v.dbg)
                        generic = pin in GENERIC_PINS
                        if v.config:
                            s["cfg"] += 1
                            s["src"][G.node_of(v.dbg)] += 1
                            s["in"] += 1
                        elif here_first and any(x[2] != "gstore" for x in others):
                            # written here first, read by later instructions: an output of this native
                            s["own"][pin] += 1
                            s["out"] += 1
                        elif not others:
                            # only this node (and its apply/revert twin) uses it: its own pin, or a value node
                            if generic:
                                s["src"][G.node_of(v.dbg)] += 1
                                s["in"] += 1
                            else:
                                s["own"][pin] += 1
                                s["in"] += 1
                        else:
                            s["src"][G.node_of(v.dbg)] += 1
                            s["in"] += 1
                    else:
                        s["computed"] += 1

    notes = {}
    if (DATA / "natives-notes.json").exists():
        notes = {int(k, 16): v for k, v in json.loads((DATA / "natives-notes.json").read_text())["notes"].items()}
    out = []
    for h, n in natives.items():
        e = ev[h]
        note = notes.get(h, {})
        args = []
        for ai, s in enumerate(e["args"]):
            onames = s["own"].most_common(1)
            nm = onames[0][0] if onames else None
            if not nm and s["src"]:
                nm = re.sub(r"\d+$", "", s["src"].most_common(1)[0][0].split("_")[0])
                nm = nm[:1].lower() + nm[1:]
            if not nm:
                nm = f"arg{ai}"
            if s["out"] > s["in"]:
                d = "out"
            elif s["in"] or s["unconnected"] or s["const1"]:
                d = "in"
            else:
                d = "?"
            a = {"name": nm, "types": [t for t, _ in s["types"].most_common()], "dir": d,
                 "evidence": {"own": dict(s["own"].most_common(4)), "from": dict(s["src"].most_common(4)),
                              "unconnected": s["unconnected"], "const1": s["const1"], "configBound": s["cfg"],
                              "readLater": s["out"], "connected": s["in"], "computed": s["computed"]}}
            ov = (note.get("args") or {}).get(str(ai))
            if ov:
                a.update(ov)
            args.append(a)
        res = {"hash": n["hash"], "name": n["name"], "node": n["node"], "argc": n["argc"], "args": args,
               "returns": note.get("returns", ""), "suspends": note.get("suspends", False),
               "doc": note.get("doc", ""), "evidence": note.get("evidence", ["data"]),
               **({"query": True} if note.get("query") else {}),
               "fn": n["fn"], "registrations": n["registrations"],
               "uses": {"calls": e["calls"], "programs": len(e["programs"]), "sections": dict(e["sections"]),
                        "resultFeeds": dict(e["next"].most_common())}}
        out.append(res)
    natives_doc = {
        "format": 1,
        "comment": "Vanilla Impact natives (tools/impact survey; docs/re/impact.md). hash = FNV-1a-32(node); "
                   "args in args[] order: args[0] is the LAST value pushed before ECALL. Evidence: [dis] the "
                   "registerNative table and Kiln's decompiles; [data] the 571 vanilla programs.",
        "build": {"kfc": g.version, "exe": reg["sha256"], "registerNative": f"0x{reg['registerNative']:x}"},
        "natives": out,
    }

    # types: every vanilla variable type + primitives, with the reflection closure
    tl = []
    quals = []
    for h, cnt in var_types.most_common():
        nm, q = names.get(h, (None, None))
        t = g.types.get(q) if q else None
        tl.append({"name": nm or f"0x{h:08x}", "hash": f"0x{h:08x}", "qualified": q, "size": t["size"] if t else None,
                   "align": t["align"] if t else None, "vanillaVariables": cnt})
        if q:
            quals.append(q)
    for p_ in PRIMS:
        h = fnv1a32(p_)
        if any(x["hash"] == f"0x{h:08x}" for x in tl):
            continue
        q = names.get(h, (None, None))[1]
        t = g.types.get(q) if q else None
        if t:
            tl.append({"name": p_, "hash": f"0x{h:08x}", "qualified": q, "size": t["size"], "align": t["align"],
                       "vanillaVariables": 0})
            quals.append(q)
    clo = g.types.closure(quals)
    recs = [{k: v for k, v in t.items() if k not in ("rva", "flags")} for t in clo["types"]]
    types_doc = {"format": 1, "comment": "Impact variable types: hash = FNV-1a-32(impact name). records: the "
                 "reflection layouts (tools/kfc TypeDb shape) for typed initial values.",
                 "build": {"exe": g.types.dump.get("exe_sha256")}, "impact": tl, "records": recs}

    cl = []
    for cid, c in sorted(cfg.items(), key=lambda kv: -len(kv[1]["programs"])):
        al = []
        for nm, _ in c["names"].most_common():
            base = re.sub(r"\d+$", "", nm)
            for x in (nm, base):
                if x and x not in al:
                    al.append(x)
        cl.append({"id": f"0x{cid:08x}", "aliases": al[:6], "types": [t for t, _ in c["types"].most_common()],
                   "programs": len(c["programs"]), "source": "program"})
    for cid, nm, ty in CPP_CONFIGS:
        if f"0x{cid:08x}" not in {x["id"] for x in cl}:
            cl.append({"id": f"0x{cid:08x}", "aliases": [nm], "types": [ty] if ty else [], "programs": 0,
                       "source": "c++ (docs/re/abilities.md)"})
    # an alias names one id: the C++ names of docs/re/abilities.md first, then the id with the most programs
    owner = {nm: (f"0x{cid:08x}", 1 << 30) for cid, nm, _ in CPP_CONFIGS}
    for c in cl:
        if c["id"] in {f"0x{cid:08x}" for cid, _, _ in CPP_CONFIGS}:
            c["aliases"] = [nm for cid, nm, _ in CPP_CONFIGS if f"0x{cid:08x}" == c["id"]] + c["aliases"]
    for c in cl:
        for a in c["aliases"]:
            if a not in owner or c["programs"] > owner[a][1]:
                owner[a] = (c["id"], c["programs"])
    for c in cl:
        c["aliases"] = [a for a in c["aliases"] if owner[a][0] == c["id"]]
    configs_doc = {"format": 1, "comment": "Config vocabulary of the vanilla programs: configId -> value-node names "
                   "(aliases usable in .kimp `config <alias>`), variable types, number of programs.",
                   "build": {"kfc": g.version}, "programs": nprog, "configs": cl}
    return natives_doc, types_doc, configs_doc
