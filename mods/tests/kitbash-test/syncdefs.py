#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A fake Kiln client checks the definitions in the server's content state (kiln.content A5, docs/kiln/sync.md).

Lab with "labPort" in kiln/config/network.json and the mods kitbash-test, placeholder-test + placeholder-items
(manifest "clients": "kiln") and newitems ("side": "both", so "clients": "mod"):

    mods/tests/kitbash-test/syncdefs.py --port 26051 [--expect-placeholders] [--verbose]

Checks: the state carries "@definitions" for placeholder-items' items and recipe (clients: kiln) with their texts
and kitbash keys, none of newitems (clients: mod: players install it), no PNG icon paths; with
--expect-placeholders the "[Missing: ...]" placeholders are sent too; a reference rule on a game item is sent as a
GUID with "options": {"refs": true}. Prints PASS/FAIL lines; exits non-zero on any FAIL.
"""
import argparse
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "sync-test"))
import synctest as st  # noqa: E402

GUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=26051)
    ap.add_argument("--expect-placeholders", action="store_true")
    ap.add_argument("--verbose", action="store_true")
    a = ap.parse_args()
    fails = 0

    def check(name, ok, detail=""):
        nonlocal fails
        fails += 0 if ok else 1
        print(f"{'PASS' if ok else 'FAIL'} syncdefs.{name}: {detail}")
        return ok

    probe = st.SyncClient(a.port, "syncdefs-probe")
    probe.hello([], st.CHANNELS)
    probe.wait(lambda: probe.welcome is not None, 8)
    if not probe.welcome:
        check("handshake", False, "no WELCOME (is the lab up with labPort set?)")
        return 2
    mods = [(m, v, s) for m, v, s in probe.welcome["mods"]]
    c = st.SyncClient(a.port, "syncdefs", a.verbose)
    if not c.connect(mods):
        check("welcome", False, "no WELCOME in 8 s")
        return 2
    c.wait(lambda: c.contents, 15)
    s = c.contents[0] if c.contents else None
    if not check("state", s is not None and "doc" in s, f"{len(c.contents)} state(s)") or not s or "doc" not in s:
        return 1
    doc = s["doc"]
    defs = doc.get("@definitions", [])
    by_name = {d["definition"].get("name"): d for d in defs}
    print(f"  {len(defs)} definition(s): {', '.join(f'{d['kind']} {n} ({d.get('mod')})' for n, d in by_name.items())}")
    ph = [n for n in ("Kiln_PH_Sword", "Kiln_PH_Ore", "Kiln_PH_Gem") if n in by_name]
    if a.expect_placeholders:
        missing = [n for n, d in by_name.items() if "placeholder" in d["definition"]]
        check("placeholders", len(missing) >= 3 and all(by_name[n]["definition"]["displayName"].startswith("[Missing: ") for n in missing),
              ", ".join(f"{n} = {by_name[n]['definition']['displayName']}" for n in missing))
    else:
        check("clients-kiln", len(ph) == 3 and "Kiln_PH_Ore_Recipe" in by_name and all(by_name[n].get("mod") == "placeholder-items" for n in ph),
              f"placeholder-items (clients: kiln): {ph + (['Kiln_PH_Ore_Recipe'] if 'Kiln_PH_Ore_Recipe' in by_name else [])}")
        sword = by_name.get("Kiln_PH_Sword", {}).get("definition", {})
        check("body", sword.get("base") == "Weapon_T1_1H_SwordEpic_01" and sword.get("model") == "item:Weapon_T4_1H_SwordEpic_02"
              and sword.get("displayName") == "Placeholder Test Sword" and sword.get("fields", {}).get("damageSetup.dmgMod") == 1.3,
              str(sword))
        ore = by_name.get("Kiln_PH_Ore", {}).get("definition", {})
        check("item-id", ore.get("guid") == "8e4d1b6a-0f93-4c27-9d5e-3a7b2c1f8e60" and ore.get("fields", {}).get("maxStackSize") == 500, str(ore))
    check("clients-mod-not-sent", not any(n.startswith("Kiln_Sample_") for n in by_name) and not any(n.startswith("Kiln_Kitbash_") for n in by_name),
          "newitems (clients: mod) and kitbash-test (server) are not sent")
    check("no-png-paths", not any(str(d["definition"].get("icon", "")).lower().endswith(".png") for d in defs), "")
    refs = [(i, b) for i, b in doc.get("items", {}).items() if isinstance(b.get("equipment.visualEntity"), str)]
    check("refs-sent", doc.get("options", {}).get("refs") is True and any(b.get("@name") == "Weapon_T2_1H_Sword_01" and GUID.match(b["equipment.visualEntity"]) for _, b in refs),
          f"options {doc.get('options')}, {len(refs)} item(s) with a visualEntity reference: {[b.get('@name') for _, b in refs][:5]}")
    c.ack_content(s["id"], 0, "syncdefs")
    c.pump(0.5)
    print(f"syncdefs: {'all PASS' if not fails else f'{fails} FAIL'}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
