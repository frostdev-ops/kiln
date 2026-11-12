#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Adds a visible usage line to the XML docs of every API member that has a thread, side or capability attribute, for
the API reference: "Thread: tick thread only · Side: server only · Capability: world.time".

  tools/docs-site/annotate.py <api folder> <stage folder>

<api folder> is what a first `docfx metadata` wrote from the stage (the attributes of each member, with their
constructor arguments, are in its YAML); the Kiln.*.xml files in <stage folder> (a copy of pack.sh's stage) are
rewritten in place, and a second `docfx metadata` over that folder puts the line into the reference. DocFX itself
shows the attributes only inside the C# declaration ("[TickThreadOnly]" above the signature), so the line would not
show without this.
The line goes at the end of the member's <summary>; a member with no summary gets it in <remarks> instead, so
undocumented.py still lists it. An attribute on a type gives the line on the type's own page only, not again on each
member. Attributes are matched by simple name (namespace and "Attribute" left off), from the one table below: a new
attribute shows up when it is in the table, with no other change. [RequiresCapability("name")] (AllowMultiple) gives
"Capability: name"; several give "Capability: a, b".
"""
import importlib.util
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

# simple attribute name -> (label, text); text None: the attribute's first constructor argument, as code. The line
# lists labels in this order.
USAGE = {
    "RequiresCapability": ("Capability", None),
    "TickThreadOnly": ("Thread", "tick thread only"),
    "AnyThread": ("Thread", "any thread"),
    "RenderThread": ("Thread", "render thread only"),
    "WorkerThread": ("Thread", "worker thread"),
    "ServerOnly": ("Side", "server only"),
    "ClientOnly": ("Side", "client only"),
    "AdvancedApi": ("Advanced", "the mod's manifest needs advanced: true"),
}
ORDER = ("Thread", "Side", "Advanced", "Capability")

spec = importlib.util.spec_from_file_location("undocumented", Path(__file__).resolve().parent / "undocumented.py")
undocumented = importlib.util.module_from_spec(spec)
spec.loader.exec_module(undocumented)


def simple(type_name):
    n = type_name.rsplit(".", 1)[-1]
    return n[:-len("Attribute")] if n.endswith("Attribute") else n


def usage_para(attributes, arguments):
    """The <para> to add for one member's attributes (type names) and their constructor arguments, or None."""
    rows = {}
    for a, args in zip(attributes, arguments):
        if simple(a) not in USAGE:
            continue
        label, text = USAGE[simple(a)]
        if text is None:
            if not args:
                continue
            text = args[0]
        if text not in rows.setdefault(label, []):
            rows[label].append(text)
    if not rows:
        return None
    p = ET.Element("para")
    p.text = ""
    last = p
    for n, label in enumerate(label for label in ORDER if label in rows):
        add = (" · " if n else "") + label + ": "
        if USAGE_CODE[label]:
            for k, value in enumerate(rows[label]):
                add += ", " if k else ""
                if last is p:
                    p.text += add
                else:
                    last.tail = (last.tail or "") + add
                last = ET.SubElement(p, "c")
                last.text = value
                add = ""
        else:
            add += ", ".join(rows[label])
            if last is p:
                p.text += add
            else:
                last.tail = (last.tail or "") + add
    return p


# labels whose values are names, shown as code
USAGE_CODE = {label: any(t is None for l, t in USAGE.values() if l == label) for label, _ in USAGE.values()}


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
            para = usage_para(item["attributes"], item["arguments"])
            if para is None:
                continue
            if member is None:
                member = ET.SubElement(tree.find("members"), "member", name=cid)
                members[cid] = member
            summary = member.find("summary")
            if summary is not None and ("".join(summary.itertext()).strip() or len(summary)):
                last = summary[-1] if len(summary) else None
                if last is None:
                    summary.text = (summary.text or "").rstrip() + " "
                else:
                    last.tail = (last.tail or "").rstrip() + " "     # a space before it in text-only views
                summary.append(para)
            else:
                remarks = member.find("remarks")
                if remarks is None:
                    remarks = ET.SubElement(member, "remarks")
                remarks.insert(0, para)
                para.tail, remarks.text = remarks.text, None
            names = {simple(a) for a in item["attributes"]}
            counts["usage"] += any(n in USAGE and USAGE[n][1] is not None for n in names)
            counts["capability"] += "RequiresCapability" in names
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
