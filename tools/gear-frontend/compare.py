#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""gear-frontend: the offline half of the gear/gearRules front-end lab (dev.32 J2).

mods/tests/gear-frontend-test writes a dump at every content ready: the served bytes of every keen::ItemInfo (SHA-256), the
keen::BalancingTable's bytes, the gear rules' items' values and registry.json. tools/gear-frontend/session.sh boots one lab
three times: "patch" (gearRules as resource patches, the front-end switched off), "front" (the same pack, the front-end on)
and "registry" (the same edits as a registry section). This tool:

    compare.py --patch <dump> --front <dump> --registry <dump> [--json <out>]
        every item and the balancing table byte-identical across the three boots; the front boot's registry.json names each
        gearRules file written by the registry (frontEnds state "registry"), the patch boot's none. Exit 1 otherwise.

Dumps hold hashes and values of the game's data: keep them in the output folder, never in the repository.
"""
import argparse
import json
import sys
from pathlib import Path


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def diff_map(name, a, b, label_a, label_b):
    """The keys whose values differ (or that one side lacks), as lines."""
    out = []
    for k in sorted(set(a) | set(b)):
        if a.get(k) != b.get(k):
            out.append(f"{name} {k}: {label_a} {str(a.get(k))[:80]} != {label_b} {str(b.get(k))[:80]}")
    return out


def balance_of(dump):
    return {g: (v or {}).get("sha256") for g, v in (dump.get("balance") or {}).items()}


def target_diffs(a, b, label_a, label_b):
    out = []
    ta, tb = a.get("targets") or {}, b.get("targets") or {}
    for name in sorted(set(ta) | set(tb)):
        va, vb = ta.get(name) or {}, tb.get(name) or {}
        for p in sorted(set(va) | set(vb)):
            if va.get(p) != vb.get(p):
                out.append(f"  {name}.{p}: {label_a} {va.get(p)} / {label_b} {vb.get(p)}")
    return out


def fronts(dump):
    reg = dump.get("registry") or {}
    return reg.get("frontEnds") or []


def compare(patch, front, registry):
    """(problems, facts): problems empty = PASS."""
    problems, facts = [], []
    if not patch.get("items") or not patch.get("balance"):
        problems.append("the patch boot's dump has no items or no balancing table")
    for label, d in (("front", front), ("registry", registry)):
        lines = diff_map("item", patch.get("items") or {}, d.get("items") or {}, "patch", label)
        lines += diff_map("balance", balance_of(patch), balance_of(d), "patch", label)
        if lines:
            problems += lines[:40]
            problems += target_diffs(patch, d, "patch", label)
        facts.append(f"patch vs {label}: {len(patch.get('items') or {})} items, {len(balance_of(patch))} balancing table(s), {len(lines)} difference(s)")
    fp, ff, fr = fronts(patch), fronts(front), fronts(registry)
    if fp:
        problems.append(f"the patch boot (front-ends off) lists front-ends: {fp}")
    if not ff or any(f.get("state") != "registry" for f in ff):
        problems.append(f"the front boot's gearRules are not all written by the registry: {ff}")
    else:
        facts.append("front boot: " + ", ".join(f"{f['mod']} {f['key']} -> registry ({f['fields']} fields)" for f in ff))
    if fr:
        problems.append(f"the registry boot (no gearRules) lists front-ends: {fr}")
    return problems, facts


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--patch", required=True)
    ap.add_argument("--front", required=True)
    ap.add_argument("--registry", required=True)
    ap.add_argument("--json")
    a = ap.parse_args(argv)
    problems, facts = compare(load(a.patch), load(a.front), load(a.registry))
    for f in facts:
        print(f)
    for p in problems:
        print("DIFFERS: " + p)
    print("gear-frontend: " + ("PASS" if not problems else "FAIL"))
    if a.json:
        Path(a.json).write_text(json.dumps({"pass": not problems, "facts": facts, "problems": problems}, indent=2), encoding="utf-8")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main())
