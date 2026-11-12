#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Lists the public types and members of the API reference that have no summary in their XML docs.

  tools/docs-site/undocumented.py [--baseline <file>] [<api folder>]     (default $KILN_OUT/docs-work/api)

Reads the metadata `docfx metadata` wrote (ManagedReference YAML, one file per type) and prints one uid per line,
sorted. DocFX lists public and protected members, so those are what is checked; namespaces have no XML docs and are
left out, as are the members the compiler writes for a record (Equals, GetHashCode, ToString, ==, !=, Deconstruct,
PrintMembers, EqualityContract, the copy constructor) and the parameterless constructor of a class that declares
none, which the compiler does not ask docs for either. A record struct looks like a plain struct in the metadata; a
struct counts as one when Equals(T), Equals(object), GetHashCode, == and != all have no summary (a hand-written
struct with exactly that gap is skipped too); its ToString then counts as the compiler's as well.
With --baseline it prints only the uids that are not in the file (one uid per line, # comments allowed) and exits 1 if
there are any. Standard library only: the YAML is read line by line, in the shape DocFX writes it.
"""
import argparse
import os
from pathlib import Path
import re
import sys

KEY = re.compile(r"^  ([A-Za-z][A-Za-z0-9.]*):(.*)$")
RECORD = re.compile(r"\brecord (struct |class )?[A-Za-z_]")
RECORD_MEMBERS = re.compile(r"\.(Equals\([^,]*\)|GetHashCode|ToString|op_Equality\(.*\)|op_Inequality\(.*\)"
                            r"|Deconstruct\(.*\)|PrintMembers\(System\.Text\.StringBuilder\)|EqualityContract)$")


def scalar(text):
    """A one-line YAML scalar as DocFX writes it: plain, 'single' or "double" quoted."""
    t = text.strip()
    if len(t) >= 2 and t[0] == t[-1] == "'":
        return t[1:-1].replace("''", "'")
    if len(t) >= 2 and t[0] == t[-1] == '"':
        import json
        return json.loads(t)
    return t


def items(path):
    """The items of one ManagedReference YAML file: dicts with uid, commentId, type, summary (True/False/None for
    absent), parent, assemblies, attributes (attribute type names) and arguments (for each attribute, in the same
    order, the values of its constructor arguments as written under `arguments:`)."""
    out, cur, key, section = [], None, None, None
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        if line and not line.startswith((" ", "-", "#")):
            section, cur = line.split(":", 1)[0], None        # items: or references:
            continue
        if line.startswith("- uid: ") and section == "items":
            cur = {"uid": scalar(line[7:]), "summary": None, "assemblies": [], "attributes": [], "arguments": []}
            out.append(cur)
            key = None
            continue
        if cur is None:
            continue
        m = KEY.match(line)
        if m:
            key, value = m.group(1), m.group(2).strip()
            if key in ("commentId", "type", "parent"):
                cur[key] = scalar(value)
            elif key == "summary":
                cur["summary"] = value not in ("", "''", '""') or None
            continue
        if line.startswith("  - "):
            if key == "assemblies":
                cur["assemblies"].append(scalar(line[4:]))
            elif key == "attributes" and line.startswith("  - type: "):
                cur["attributes"].append(scalar(line[10:]))
                cur["arguments"].append([])
                cur["in_args"] = False
        elif key == "attributes" and cur["attributes"]:
            # under one attribute:     arguments:  /    - type: System.String  /      value: name
            if re.match(r"    [A-Za-z]", line):
                cur["in_args"] = line.startswith("    arguments:")
            elif cur.get("in_args") and line.startswith("      value: "):
                cur["arguments"][-1].append(scalar(line[13:]))
        elif key == "syntax" and cur.get("type") in ("Class", "Struct"):
            if line.startswith("    ") and not line.startswith("     "):
                cur["content"] = line.startswith("    content:")      # the C# declaration, maybe on several lines
            if cur.get("content") and RECORD.search(line):
                cur["record"] = True
        elif key == "summary" and cur["summary"] is None and line.strip():
            cur["summary"] = True      # a block scalar (summary: >-) whose text starts on the next line
    return out


def generated(item, records):
    """A member the compiler writes: a record's equality members, or the parameterless constructor of a class that
    declares none (the metadata cannot tell it from a hand-written one, so an undocumented one is skipped too)."""
    uid = item["uid"]
    if item.get("type") == "Constructor" and uid.endswith(".#ctor"):
        return True
    if item.get("parent") not in records:
        return False
    if RECORD_MEMBERS.search(uid):
        return True
    return item.get("type") == "Constructor" and uid == f"{item['parent']}.#ctor({item['parent']})"


def undocumented(api):
    every = [i for f in sorted(Path(api).glob("*.yml")) if f.name != "toc.yml" for i in items(f)]
    records = {i["uid"] for i in every if i.get("record")}
    # a record struct reads as a plain struct in the metadata: it is one when it has the whole undocumented set of
    # members the compiler writes for a record
    bare = {i["uid"] for i in every if not i["summary"]}
    for t in every:
        u = t["uid"]
        if t.get("type") == "Struct" and {f"{u}.Equals({u})", f"{u}.Equals(System.Object)", f"{u}.GetHashCode",
                                          f"{u}.op_Equality({u},{u})", f"{u}.op_Inequality({u},{u})"} <= bare:
            records.add(u)
    return sorted({i["uid"] for i in every
                   if i.get("type") not in (None, "Namespace") and not i["summary"] and not generated(i, records)})


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("api", nargs="?", help="the api folder docfx metadata wrote (default $KILN_OUT/docs-work/api)")
    ap.add_argument("--baseline", help="uids already known to have no summary")
    a = ap.parse_args()
    api = a.api or (os.environ.get("KILN_OUT") and os.path.join(os.environ["KILN_OUT"], "docs-work", "api"))
    if not api or not Path(api).is_dir():
        sys.exit("no api folder: give one or set KILN_OUT")
    found = undocumented(api)
    if a.baseline:
        known = {ln.strip() for ln in Path(a.baseline).read_text(encoding="utf-8").splitlines()
                 if ln.strip() and not ln.startswith("#")}
        found = [u for u in found if u not in known]
    for u in found:
        print(u)
    sys.exit(1 if a.baseline and found else 0)


if __name__ == "__main__":
    main()
