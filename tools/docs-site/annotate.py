#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Adds a visible usage line to the XML docs of every API member that has a thread or side attribute or names a
capability, for the API reference: "Thread: tick thread only · Side: server only · Capability: world.time".

  tools/docs-site/annotate.py <api folder> <stage folder>

<api folder> is what a first `docfx metadata` wrote from the stage (the attributes of each member are in its YAML);
the Kiln.*.xml files in <stage folder> (a copy of pack.sh's stage) are rewritten in place, and a second
`docfx metadata` over that folder puts the line into the reference. DocFX itself shows the attributes only inside the
C# declaration ("[TickThreadOnly]" above the signature) and drops XML doc tags it does not know, so neither the line
nor the capability would show without this.
The line goes at the end of the member's <summary>; a member with no summary gets it in <remarks> instead, so
undocumented.py still lists it. Attributes are matched by simple name (namespace and "Attribute" left off), from the
one table below: a new attribute shows up when it is in the table, with no other change.
"""
import importlib.util
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

# simple attribute name -> (label, text); the line lists labels in this order
USAGE = {
    "TickThreadOnly": ("Thread", "tick thread only"),
    "AnyThread": ("Thread", "any thread"),
    "RenderThread": ("Thread", "render thread only"),
    "WorkerThread": ("Thread", "worker thread"),
    "ServerOnly": ("Side", "server only"),
    "ClientOnly": ("Side", "client only"),
    "AdvancedApi": ("Advanced", "the mod's manifest needs advanced: true"),
}
ORDER = ("Thread", "Side", "Advanced")

spec = importlib.util.spec_from_file_location("undocumented", Path(__file__).resolve().parent / "undocumented.py")
undocumented = importlib.util.module_from_spec(spec)
spec.loader.exec_module(undocumented)


def capability(member):
    """The capability a member needs, from its XML docs, or None. The one place that knows the form:
    <capability>name</capability>, anywhere in the member's doc comment."""
    for e in member.iter("capability"):
        if (e.text or "").strip():
            return e.text.strip()
    return None


def drop_capability(member):
    for parent in member.iter():
        for e in [c for c in parent if c.tag == "capability"]:
            parent.remove(e)


def simple(type_name):
    n = type_name.rsplit(".", 1)[-1]
    return n[:-len("Attribute")] if n.endswith("Attribute") else n


def usage_para(attributes, cap):
    """The <para> to add, or None when there is nothing to say."""
    rows = {}
    for a in attributes:
        if simple(a) in USAGE:
            label, text = USAGE[simple(a)]
            rows.setdefault(label, []).append(text)
    if not rows and not cap:
        return None
    p = ET.Element("para")
    parts = [f"{label}: {', '.join(rows[label])}" for label in ORDER if label in rows]
    p.text = " · ".join(parts)
    if cap:
        p.text += (" · " if parts else "") + "Capability: "
        c = ET.SubElement(p, "c")
        c.text = cap
    return p


def annotate(api, stage):
    docs = {}       # assembly name -> (path, tree, {member name: element})
    for f in sorted(Path(stage).glob("Kiln.*.xml")):
        tree = ET.parse(f)
        name = (tree.findtext("assembly/name") or f.stem).strip()
        docs[name] = (f, tree, {m.get("name"): m for m in tree.iter("member")})
    items = [i for f in sorted(Path(api).glob("*.yml")) if f.name != "toc.yml" for i in undocumented.items(f)]
    counts = {"usage": 0, "capability": 0}
    changed = set()
    for item in items:
        cid = item.get("commentId")
        for asm in item["assemblies"] or []:
            if asm not in docs or not cid:
                continue
            path, tree, members = docs[asm]
            member = members.get(cid)
            cap = capability(member) if member is not None else None
            para = usage_para(item["attributes"], cap)
            if para is None:
                continue
            if member is None:
                member = ET.SubElement(tree.find("members"), "member", name=cid)
                members[cid] = member
            drop_capability(member)
            summary = member.find("summary")
            if summary is not None and ("".join(summary.itertext()).strip() or len(summary)):
                summary.append(para)
            else:
                remarks = member.find("remarks")
                if remarks is None:
                    remarks = ET.SubElement(member, "remarks")
                remarks.insert(0, para)
                para.tail, remarks.text = remarks.text, None
            counts["usage"] += any(simple(a) in USAGE for a in item["attributes"])
            counts["capability"] += cap is not None
            changed.add(asm)
    for asm in changed:
        path, tree, _ = docs[asm]
        tree.write(path, encoding="utf-8", xml_declaration=True)
    return counts


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__.split("\n\n")[1])
    c = annotate(sys.argv[1], sys.argv[2])
    print(f"annotate: {c['usage']} members with a thread/side line, {c['capability']} with a capability")


if __name__ == "__main__":
    main()
