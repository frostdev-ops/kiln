#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Adds lines to the XML docs of the API reference that DocFX cannot make by itself: for each member, "Thread: tick thread
only · Side: server only · Capability: world.time" (from its attributes) and "Since mod API 0.2.12 (0.2.0-dev.30)"; for an
extension member, the call shape of the receiver; for each type, the samples that use it and, when it is the receiver of
extension members, a list of them.

  tools/docs-site/annotate.py [--repo <repository>] <api folder> <stage folder>

<api folder> is what a first `docfx metadata` wrote from the stage (the attributes of each member, with their
constructor arguments, are in its YAML); the Kiln.*.xml files in <stage folder> (a copy of pack.sh's stage) are
rewritten in place, and a second `docfx metadata` over that folder puts the lines into the reference. DocFX itself
shows the attributes only inside the C# declaration ("[TickThreadOnly]" above the signature), so the line would not
show without this.
The lines go at the end of the member's <summary>; a member with no summary gets them in <remarks> instead, so
undocumented.py still lists it. An attribute on a type gives the line on the type's own page only, not again on each
member. Attributes are matched by simple name (namespace and "Attribute" left off), from the one table below: a new
attribute shows up when it is in the table, with no other change. [RequiresCapability("name")] (AllowMultiple) gives
"Capability: name"; several give "Capability: a, b".
With --repo (the repository to read: managed/api-history.tsv, the PublicAPI files, mods/samples) the since lines, the
extension notes and the sample links are added too (since.py, apiids.py, samples.py); without it only the attribute lines.
  Since mod API 0.2.12 (0.2.0-dev.30)       every type and member the ledger or PublicAPI.Unshipped.txt knows (since.py)
  Extension member of Is: call it as Is.InCombat       on AiRulesApi.get_InCombat, the static method the compiler makes
  Extension members                          on Is: what other classes add to it, with links (remarks)
  Used in samples: Hello/HelloMod.cs, ...    on each type, with links to the sample's file on GitHub (remarks)
  Tier: Stable (...)                          on each type: its tier (docs/kiln/modding/tiers.md), from the repository's
                                              tools/docs-site/tiers.json (its assembly's default, or the type's own row)
"""
import copy
import importlib.util
import json
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


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).resolve().parent / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


undocumented = load("undocumented", "undocumented.py")
docxml = load("docxml", "docxml.py")
apiids = load("apiids", "apiids.py")
since_mod = load("since", "since.py")
samples_mod = load("samples", "samples.py")


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


TYPES = ("Class", "Struct", "Enum", "Interface", "Delegate")
# The tiers (docs/kiln/modding/tiers.md, decision D5) as a type's line says them.
TIER_TEXT = {
    "stable": "Stable (Kiln's names: survives game updates; its contract is frozen with API 1.0.0)",
    "advanced": "Advanced (the game's names: verified per game build, outside the update promise)",
    "native": "Native (addresses, raw memory, native code: the mod's own risk on every game update)",
}


def load_tiers(repo):
    """tools/docs-site/tiers.json of the repository ({"assemblies": {name: tier}, "types": {full name: tier}}), or None."""
    p = Path(repo) / "tools" / "docs-site" / "tiers.json"
    if not p.is_file():
        return None
    return json.loads(p.read_text(encoding="utf-8"))


def tier_of(tiers, item):
    """A type's tier: its own row (generic arity left out), else its assembly's; None when tiers.json names neither."""
    name = item["uid"].split("`")[0]
    if name in tiers.get("types", {}):
        return tiers["types"][name]
    for asm in item.get("assemblies") or []:
        if asm in tiers.get("assemblies", {}):
            return tiers["assemblies"][asm]
    return None
SAMPLES_SHOWN = 8


def sample_notes(repo_samples, item):
    """The "Used in samples" <para> of a type, or None. A sample names a type inside its namespace: `Chat`, `Abilities.Attributes`."""
    uid, namespace = item["uid"], item.get("namespace") or ""
    qualified = uid[len(namespace) + 1:] if namespace and uid.startswith(namespace + ".") else uid
    files = repo_samples.use(".".join(part.split("`")[0] for part in qualified.split(".")))
    if not files:
        return None
    parts = ["Used in samples: "]
    for n, rel in enumerate(files[:SAMPLES_SHOWN]):
        parts.append(", " if n else "")
        parts.append(docxml.link(samples_mod.url(rel), samples_mod.label(rel)))
    if len(files) > SAMPLES_SHOWN:
        parts.append(f" and {len(files) - SAMPLES_SHOWN} more")
    return docxml.para(*parts)


def extension_members(repo):
    """The extension members of the repository's PublicAPI files: [dict of apiids.extension_member, ...]."""
    out = []
    for path in sorted((Path(repo) / "managed").rglob("PublicAPI.*.txt")):
        if {"bin", "obj"} & set(path.parts) or path.name not in ("PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt"):
            continue
        for line in since_mod.record_lines(path.read_text(encoding="utf-8-sig")):
            e = apiids.extension_member(line)
            if e:
                out.append(e)
    return out


def call_shape(e):
    """`Is.InCombat`, `Is.AlliesNear(int count, double metres)`: how an extension member is written in code."""
    receiver = e["receiver"].split("<")[0].rsplit(".", 1)[-1]
    return f"{receiver}.{e['name']}" + ("" if e["kind"] == "property" else f"({e['parameters']})")


def annotate(api, stage, repo=None):
    docs = {}       # assembly name -> (path, tree, {member name: element})
    for f in sorted(Path(stage).glob("Kiln.*.xml")):
        tree = ET.parse(f)
        name = (tree.findtext("assembly/name") or f.stem).strip()
        docs[name] = (f, tree, {m.get("name"): m for m in tree.iter("member")})
    items = [i for f in sorted(Path(api).glob("*.yml")) if f.name != "toc.yml" for i in undocumented.items(f)]
    counts = {"usage": 0, "capability": 0, "since": 0, "extension": 0, "receivers": 0, "samples": 0, "tier": 0}
    tiers = load_tiers(repo) if repo else None
    changed = set()
    since = since_mod.Since(repo) if repo else None
    repo_samples = samples_mod.Samples(repo) if repo else None
    by_key, ext_by_key, receivers = {}, {}, {}
    if repo:
        for i in items:
            if i.get("commentId") and i.get("type") != "Namespace":
                try:
                    key = apiids.key_of_doc_id(i["commentId"])
                except Exception:
                    continue
                if key:
                    by_key.setdefault(key, i)
        for e in extension_members(repo):
            member = by_key.get(e["key"])
            if member is None:
                continue
            ext_by_key[e["key"]] = e
            e["item"] = member
            receiver = by_key.get(("T", apiids.generics_to_arity(e["receiver"]), "", ""))
            if receiver is not None:
                receivers.setdefault(receiver["commentId"], (receiver, []))[1].append(e)
    for item in items:
        cid = item.get("commentId")
        if not cid:
            continue
        paras = []
        usage = usage_para(item["attributes"], item["arguments"])
        if usage is not None:
            paras.append(usage)
        if tiers is not None and item.get("type") in TYPES:
            tier = tier_of(tiers, item)
            if tier in TIER_TEXT:
                paras.append(docxml.para("Tier: " + TIER_TEXT[tier]))
                counts["tier"] += 1
        if since is not None and item.get("type") != "Namespace":
            text = since.text(cid)
            if text:
                paras.append(docxml.para(text))
                counts["since"] += 1
        shape = None
        if repo:
            try:
                shape = ext_by_key.get(apiids.key_of_doc_id(cid))
            except Exception:
                shape = None
        if shape is not None:
            receiver = by_key.get(("T", apiids.generics_to_arity(shape["receiver"]), "", ""))
            name = shape["receiver"].split("<")[0].rsplit(".", 1)[-1]
            paras.append(docxml.para("Extension member of ", docxml.see(receiver["commentId"], name) if receiver else name,
                                     ": in code it reads ", docxml.code(call_shape(shape)),
                                     " (an instance extension member of the receiver)." if shape["instance"] else "."))
            counts["extension"] += 1
        remarks = []
        if cid in receivers:
            remarks += receiver_notes(receivers[cid][1], docs)
            counts["receivers"] += 1
        if repo_samples is not None and item.get("type") in TYPES:
            note = sample_notes(repo_samples, item)
            if note is not None:
                remarks.append(note)
                counts["samples"] += 1
        if not paras and not remarks:
            continue
        for asm in item["assemblies"] or []:
            if asm not in docs:
                continue
            path, tree, members = docs[asm]
            if item["summary"]:
                keep_summary(tree, members, cid)
            docxml.add_to_summary(tree, members, cid, [copy_of(p) for p in paras])
            docxml.add_to_remarks(tree, members, cid, [copy_of(r) for r in remarks])
            changed.add(asm)
        names = {simple(a) for a in item["attributes"]}
        counts["usage"] += any(n in USAGE and USAGE[n][1] is not None for n in names)
        counts["capability"] += "RequiresCapability" in names
    for asm in changed:
        path, tree, _ = docs[asm]
        tree.write(path, encoding="utf-8", xml_declaration=True)
    return counts


def copy_of(element):
    return copy.deepcopy(element)


def keep_summary(tree, members, cid):
    """DocFX gives some members a summary the XML docs do not hold under their own name: a positional record's property has the
    <param> of the record, an override or an implementation the summary of the member it overrides or implements. A <member>
    made for the lines added here would hide that, so the summary is written into it (the <param>) or asked for
    (<inheritdoc/>, which DocFX resolves the same way). The first YAML (before any line is added) says which members have one."""
    member = members.get(cid)
    if member is not None and (docxml.has_text(member.find("summary")) or member.find("inheritdoc") is not None):
        return
    if cid.startswith("P:") and "." in cid:
        owner, name = cid[2:].rsplit(".", 1)
        record = members.get("T:" + owner)
        param = next((p for p in record.findall("param") if p.get("name") == name), None) if record is not None else None
        if param is not None:
            summary = ET.SubElement(docxml.member_of(tree, members, cid), "summary")
            summary.text = param.text
            summary.extend(copy_of(child) for child in param)
            return
    ET.SubElement(docxml.member_of(tree, members, cid), "inheritdoc")


def summary_of(cid, docs, depth=0):
    """The first sentence of a member's summary: a compiled extension member's doc is an <inheritdoc cref> of the member in
    the compiler's marker class, which has the text."""
    for path, tree, found in docs.values():
        member = found.get(cid)
        if member is None:
            continue
        summary = member.find("summary")
        if summary is not None and docxml.plain(summary):
            return docxml.first_sentence(summary)
        inherit = member.find("inheritdoc")
        if inherit is not None and inherit.get("cref") and depth < 2:
            return summary_of(inherit.get("cref"), docs, depth + 1)
    return None


def receiver_notes(members, docs):
    """The "Extension members" paragraphs and lists of a type that other classes add members to: one list per class that adds
    them, with a link and the first sentence of the summary of each."""
    out = [docxml.para(docxml.bold("Extension members."), " Other classes add members to this type. In code they read as its own "
                       "(C# 14 extension members); each is documented on the class that declares it, which the links lead to.")]
    owners = {}
    for e in members:
        owners.setdefault(e["owner"], []).append(e)
    for owner in sorted(owners):
        entries = owners[owner]
        assemblies = entries[0]["item"]["assemblies"]
        head = docxml.para("From ", docxml.see("T:" + owner, owner.rsplit(".", 1)[-1]),
                           f" ({assemblies[0]}):" if assemblies else ":")
        listing = ET.Element("list", type="bullet")
        seen = set()
        for e in sorted(entries, key=lambda x: (x["name"], x["accessor"] != "get", x["key"])):
            label = call_shape(e)
            if label in seen:
                continue
            seen.add(label)
            cid = e["item"]["commentId"]
            doc = summary_of(cid, docs)
            row = ET.SubElement(ET.SubElement(listing, "item"), "description")
            row.append(docxml.see(cid, label))
            row[-1].tail = (": " + doc) if doc else ""
        out += [head, listing]
    return out


def main():
    args = sys.argv[1:]
    repo = None
    if len(args) >= 2 and args[0] == "--repo":
        repo, args = args[1], args[2:]
    if len(args) != 2:
        sys.exit(__doc__.split("\n\n")[1])
    c = annotate(args[0], args[1], repo)
    print(f"annotate: {c['usage']} members with a thread/side line, {c['capability']} with a capability"
          + (f", {c['since']} with a since line, {c['extension']} extension members, {c['receivers']} receivers of them, "
             f"{c['samples']} types with samples, {c['tier']} types with a tier" if repo else ""))


if __name__ == "__main__":
    main()
