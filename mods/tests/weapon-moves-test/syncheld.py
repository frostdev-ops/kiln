#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A fake Kiln client reads the content state a lab server sends (docs/kiln/sync.md) and checks the held models with moving parts in it.

Lab with "labPort" in kiln/config/network.json, weapon-moves-test (and the sample weapon-moves, when it is loaded):

    mods/tests/weapon-moves-test/syncheld.py --port <labPort> [--verbose] [--dump FILE] [--fail-once]

Each hand's moving template (<mod>.<item>.moving, a pair's .moving.l and .moving.r) keeps its model and its graph on each machine: the body of rules
the state carries under its GUID (the server's layer on a client) has no ModelResource.model and no AnimationGraphResourceComponent2.animationGraph, and
still has its other values that differ from the original (the hand, the shield's VFX); the template is in "templates"."add". The clone's definition in
"add" may carry the two fields (a client with its own definition keeps its own): printed as INFO lines. Prints PASS/FAIL lines; exits non-zero on any FAIL.
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "sync-test"))
import synctest as st  # noqa: E402
import nettest as nt  # noqa: E402

MARKED = ("ModelResource.model", "AnimationGraphResourceComponent2.animationGraph")
TEST = [("weapon-moves-test", b, pair) for b, pair in (("held-sword", False), ("held-greataxe", False), ("held-shield", False), ("held-daggers", True), ("held-daggers-left", True))]
SAMPLE = [("weapon-moves", b, False) for b in ("turning-blade", "turning-blade-json")]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--verbose", action="store_true")
    ap.add_argument("--dump", help="write the state the server sent to this file")
    ap.add_argument("--fail-once", action="store_true", help="one expectation wrong, to see the check fail")
    a = ap.parse_args()
    fails = 0

    def check(name, ok, detail=""):
        nonlocal fails
        fails += 0 if ok else 1
        print(f"{'PASS' if ok else 'FAIL'} syncheld.{name}: {detail}")

    probe = st.SyncClient(a.port, "syncheld-probe")
    probe.hello(nt.with_required(a.port, [], st.CHANNELS), st.CHANNELS)
    probe.wait(lambda: probe.welcome is not None, 8)
    if not probe.welcome:
        check("handshake", False, "no WELCOME (is the lab up with labPort set?)")
        return 2
    mods = [m for m, _, _ in probe.welcome["mods"]]
    c = st.SyncClient(a.port, "syncheld", a.verbose)
    if not c.connect([(m, v, sv) for m, v, sv in probe.welcome["mods"]]):
        check("welcome", False, "no WELCOME in 8 s")
        return 2
    c.wait(lambda: c.contents, 15)
    state = c.contents[0] if c.contents else None
    if not state or "doc" not in state:
        check("state", False, "no content state")
        return 1
    doc = state["doc"]
    if a.dump:
        with open(a.dump, "w") as f:
            json.dump(doc, f, indent=1)
    templates = doc.get("templates", {})
    adds = {x.get("name"): x for x in templates.get("add", [])}
    bodies = {}
    for key, v in templates.items():
        if isinstance(v, dict):
            bodies.setdefault(v.get("@name") or key, []).append((key, v))
    print(f"syncheld: state {state.get('sid', '?')}, {len(adds)} template clone(s) in add, {len(bodies)} template bodies; mods {mods}")

    cases = TEST + (SAMPLE if "weapon-moves" in mods else [])
    for mod, block, pair in cases:
        base = f"{mod}.{block}.moving"
        hands = [f"{base}.l", f"{base}.r"] if pair else [base]
        for name in hands:
            entry = adds.get(name)
            check(f"{name}.add", entry is not None, f"in templates.add: {entry is not None} (base {entry.get('base') if entry else None})")
            fields = dict((entry or {}).get("fields", {}))
            rules = {}
            for key, body in bodies.get(name, []) + bodies.get((entry or {}).get("guid", "-"), []):
                rules.update({k: v for k, v in body.items() if not k.startswith("@")})
            if a.verbose:
                print(f"  {name}: fields {sorted(fields)}; rules {sorted(rules)}")
            # the body (the server's layer on a client) must not carry them; the clone's definition in add may: it gives the fields only to a client with no
            # definition of its own (native/services/content/gamedata_clones.cpp), so it is shown, not checked
            sent = [k for k in rules if k in MARKED]
            if a.fail_once and name == "weapon-moves-test.held-sword.moving":
                sent.append("(fail-once)")
            check(f"{name}.machine-local", entry is not None and not sent, f"its body sends no model and no graph: {sent}")
            print(f"INFO syncheld.{name}.definition: the clone's definition in add carries {[(k, fields[k]) for k in MARKED if k in fields]}")
            # an unmarked value of the same template that differs from its original still syncs: the hand (where it is not the staff's right hand), the VFX of
            # a look whose VFX the staff does not have
            right = name.endswith(".r") or block in ("held-sword", "turning-blade", "turning-blade-json")
            hand = rules.get("SlotAttachment.attachmentSlot")
            if not right:
                check(f"{name}.hand", hand is not None, f"its body still sends the hand: SlotAttachment.attachmentSlot = {hand}")
            if block == "held-shield":
                vfx = rules.get("VfxComponentResource.vfx")
                check(f"{name}.vfx", isinstance(vfx, str) and len(vfx) == 36, f"its body still sends the look's VFX (not the staff's own): VfxComponentResource.vfx = {vfx}")
        if pair:
            parent = adds.get(base)
            check(f"{base}.add", parent is not None, f"the pair's parent is in templates.add (base {parent.get('base') if parent else None})")

    print(f"syncheld: {fails} FAIL")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
