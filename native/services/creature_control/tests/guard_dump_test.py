#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""kiln.creature_control's layout guard (types_guard.h, kGuardPaths) against the committed type dumps of the verified
game builds (sigdb/types/<side>/<exe sha256>.json, structure only): every path resolves by name, base classes and
typedefs walked as the service walks them through kiln.types, to the offset, size or enum value the table expects.
A dump without the root types (a partial one) is skipped and named. No game, no Wine.
    python3 native/services/creature_control/tests/guard_dump_test.py"""
import json
import pathlib
import re
import sys

here = pathlib.Path(__file__).resolve().parent
repo = here.parents[3]
table = (here.parent / "types_guard.h").read_text()
body = table[table.index("guard-table-begin"):table.index("guard-table-end")]
rows = re.findall(r'\{"([^"]+)", "([^"]*)", (0x[0-9a-fA-F]+|\d+), (\d+), GuardUse::(\w+)\}', body)
assert len(rows) >= 15, f"read {len(rows)} guard rows"


def resolve(types, root, path):
    t = types.get(root)
    if t is None:
        return None, "no type " + root
    if path == "":
        return (t["size"], 0), None
    if path.startswith("::"):
        for v in t.get("values", []):
            if v["name"] == path[2:]:
                return (v["value"], 0), None
        return None, "no value " + path
    off = 0
    for seg in path.split("."):
        cur, found = t, None
        for _ in range(16):
            if cur is None:
                break
            if cur.get("kind") == 17 and not cur.get("fields"):  # a typedef: what it names
                cur = types.get(cur.get("inner"))
                continue
            found = next((f for f in cur.get("fields", []) if f["name"] == seg), None)
            if found or cur.get("kind") != 18:
                break
            cur = types.get(cur.get("inner"))  # the base class
        if not found:
            return None, "no field " + seg
        off += found["offset"]
        t = types.get(found["type"])
        if t is None:
            return None, "no type " + found["type"]
    return (off, t["size"]), None


checked, failures = 0, []
for dump in sorted((repo / "sigdb" / "types").glob("*/*.json")):
    d = json.loads(dump.read_text())
    types = {t["qualifiedName"]: t for t in d.get("types", [])}
    roots = {r[0] for r in rows}
    if not roots <= set(types):
        print(f"skip {dump.relative_to(repo)}: no {', '.join(sorted(roots - set(types)))[:120]}")
        continue
    for root, path, expect, size, use in rows:
        got, why = resolve(types, root, path)
        want = (int(expect, 0), int(size))
        label = f"{dump.parent.name} {root}{'.' if path and not path.startswith('::') else ''}{path or ' size'}"
        if got != want:
            failures.append(f"{label}: {why or got} != {want} ({use})")
    checked += 1
    print(f"ok {dump.relative_to(repo)}: {len(rows)} guard paths match")
for f in failures:
    print("FAIL " + f)
if not checked:
    print("FAIL no type dump has the guard's root types")
sys.exit(1 if failures or not checked else 0)
