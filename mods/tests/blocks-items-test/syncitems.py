#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A fake Kiln client checks that item blocks reach a Kiln client through the server's content state (docs/kiln/sync.md).

Lab with "labPort" in kiln/config/network.json and the mod item-blocks ("clients": "kiln"):

    mods/tests/blocks-items-test/syncitems.py --port 26511 [--verbose]

Checks: "@definitions" carries the items and recipes; the "add" lists carry the templates, sequences and buffs they use; the values a block sets by rule
(tooltip rows, configs, sequence bindings) are in the state. Prints PASS/FAIL lines; exits non-zero on any FAIL.
"""
import argparse
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
    print(f"syncitems: {fails} FAIL")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
