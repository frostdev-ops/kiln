#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A fake Kiln client checks that item blocks reach a Kiln client through the server's content state (docs/kiln/sync.md).

Lab with "labPort" in kiln/config/network.json and the mod item-blocks ("clients": "kiln"):

    mods/tests/blocks-items-test/syncitems.py --port 26511 [--lab <lab folder>] [--verbose]

Checks: "@definitions" carries the items and recipes, with their texts in every language; the "add" lists carry the templates, sequences and buffs they use;
"@programs" carries every program those name (with --lab: byte for byte the ones the blocks made, and nothing else); the values a block sets by rule
(tooltip rows, configs, sequence bindings, a food's own time) are in the state. Prints PASS/FAIL lines; exits non-zero on any FAIL.
"""
import argparse
import base64
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "sync-test"))
import synctest as st  # noqa: E402
import nettest as nt  # noqa: E402

ITEMS = ["ember-shard", "healing-draught", "ember-tonic", "ember-stew", "ember-arrow", "ember-axe", "ember-pick", "ember-gem", "sword", "waraxe", "mace", "greatsword",
         "greataxe", "hammer", "daggers", "bow", "staff", "ember-guard", "ember-edge", "ember-wand", "ember-helm", "ember-mail", "ember-band", "ember-bench"]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=26511)
    ap.add_argument("--verbose", action="store_true")
    ap.add_argument("--lab", help="the lab's folder: also compare the programs sent with the ones blocks-items-test wrote")
    a = ap.parse_args()
    fails = 0

    def check(name, ok, detail=""):
        nonlocal fails
        fails += 0 if ok else 1
        print(f"{'PASS' if ok else 'FAIL'} syncitems.{name}: {detail}")

    probe = st.SyncClient(a.port, "syncitems-probe")
    probe.hello(nt.with_required(a.port, [], st.CHANNELS), st.CHANNELS)
    probe.wait(lambda: probe.welcome is not None, 8)
    if not probe.welcome:
        check("handshake", False, "no WELCOME (is the lab up with labPort set?)")
        return 2
    c = st.SyncClient(a.port, "syncitems", a.verbose)
    if not c.connect([(m, v, sv) for m, v, sv in probe.welcome["mods"]]):
        check("welcome", False, "no WELCOME in 8 s")
        return 2
    c.wait(lambda: c.contents, 15)
    state = c.contents[0] if c.contents else None
    if not state or "doc" not in state:
        check("state", False, "no content state")
        return 1
    doc = state["doc"]
    if a.verbose:
        print(json.dumps(doc, indent=1)[:4000])
    defs = doc.get("@definitions", [])
    by_name = {d.get("definition", {}).get("name"): d for d in defs}
    for n in ITEMS:
        d = by_name.get(f"item-blocks.{n}")
        check(f"item.{n}", d is not None and d.get("kind") in ("item", "items") and d.get("mod") == "item-blocks", f"{d.get('kind') if d else 'missing'}")
    check("recipes", all(f"item-blocks.{n}.recipe" in by_name for n in ["sword", "ember-wand", "ember-mail", "ember-gem"]), "sent with the items")

    def added(section):
        add = doc.get(section, {}).get("add", [])
        return {x.get("name") for x in add}
    templates, sequences, buffs = added("templates"), added("sequences"), added("buffs")
    check("templates", {"item-blocks.ember-wand.primary.projectile", "item-blocks.ember-wand.heavy.impact", "item-blocks.ember-bench.station"} <= templates, f"{len(templates)} template clone(s)")
    check("sequences", {"item-blocks.ember-wand.primary", "item-blocks.ember-tonic.use", "item-blocks.ember-stew.use"} <= sequences, f"{len(sequences)} sequence clone(s)")
    check("buffs", {"item-blocks.burn", "item-blocks.warmth"} <= buffs, f"{len(buffs)} buff clone(s)")

    def body(section, name):
        for key, v in doc.get(section, {}).items():
            if isinstance(v, dict) and v.get("@name") == name:
                return v
        return None
    mail = body("items", "item-blocks.ember-mail") or {}
    check("rows", any(k.startswith("uiValues") for k in mail), f"ember-mail's rows: {[k for k in mail if k.startswith('uiValues')][:3]}")
    wand = body("items", "item-blocks.ember-wand") or {}
    wand_refs = body("itemdata", "item-blocks.ember-wand") or {}
    check("wand-binding", any("sequences[0]" in k for k in list(wand) + list(wand_refs)), "the wand's primary is bound to its own sequence")
    heavy = body("sequences", "item-blocks.ember-wand.heavy") or {}
    tags = [v for k, v in heavy.items() if k.endswith(".requiredEntityTag")]
    check("cleared-ref", bool(tags) and all(v == "none" for v in tags), f"a cleared reference is sent as none: {tags}")
    stew = body("itemdata", "item-blocks.ember-stew") or {}
    check("food-buff", any("appliedBuff" in k for k in stew), f"{list(stew)[:3]}")
    stew_cfg = {k: v for k, v in (body("items", "item-blocks.ember-stew") or {}).items() if "0xbee6d4a9" in k or "PrimerBuffDuration" in k}
    check("food-time", list(stew_cfg.values()) == [600] or list(stew_cfg.values()) == [600.0], f"the stew's own food time: {stew_cfg}")

    # a status: its look's base and no text of that base
    warmth = next((x for x in doc.get("buffs", {}).get("add", []) if x.get("name") == "item-blocks.warmth"), {})
    wf = warmth.get("fields", {})
    check("buff-look", warmth.get("base") == "0xd98a51cb" and wf.get("hideDurationInUi") is False and "description" in wf,
          f"base {warmth.get('base')}, hideDurationInUi {wf.get('hideDurationInUi')}, description {wf.get('description')}")

    # a placeable item names its own template, and that template comes in the same state: a client never places the starter's
    clones = {x.get("guid"): x.get("name") for x in doc.get("templates", {}).get("add", [])}
    for station in ("ember-bench", "ember-bench-json"):
        refs = ((by_name.get(f"item-blocks.{station}") or {}).get("definition") or {}).get("refs") or {}
        placed = refs.get("equipment.placedEntity")
        check(f"placed.{station}", clones.get(placed) == f"item-blocks.{station}.station",
              f"equipment.placedEntity {placed} is {clones.get(placed, 'no template of the state')}")

    # texts beyond the name travel with the definition
    gem = (by_name.get("item-blocks.ember-gem") or {}).get("definition", {})
    de = gem.get("texts", {}).get("de", {})
    check("texts", de.get("name") == "Glutstein" and de.get("description") == "Noch warm.", f"ember-gem texts.de: {de}")

    # every program a clone names comes along, as the server serves it, and no game program does
    named = set()
    for section in ("templates", "sequences", "buffs"):
        for x in doc.get(section, {}).get("add", []):
            if not str(x.get("name", "")).startswith("item-blocks."):
                continue
            for k, v in x.get("fields", {}).items():
                if (k.endswith("Impact.program") or k.endswith("].impact") or k == "impact") and isinstance(v, str) and len(v) == 36:
                    named.add(v)
    programs = {p.get("guid"): p.get("data") for p in doc.get("@programs", [])}
    missing = sorted(g for g in named if g not in programs)
    check("programs", bool(named) and not missing, f"{len(named)} named by item-blocks' clones, {len(programs)} sent; missing {missing}")
    if a.lab:
        # blocks-items-test writes every generated program there: what is sent must be one of them, byte for byte (so never a game program)
        dump = os.path.join(a.lab, "kiln", "data", "blocks-items-test", "programs")
        same, bad, other = 0, [], []
        for g, data in programs.items():
            path = os.path.join(dump, f"{g}.bin")
            if not os.path.exists(path):
                other.append(g)
            elif open(path, "rb").read() == base64.b64decode(data):
                same += 1
            else:
                bad.append(g)
        check("program-bytes", same >= len(named) and not bad and not other,
              f"{same} byte-identical to the generated programs; different {bad}; not generated by a block {other}")
    print(f"syncitems: {fails} FAIL")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
