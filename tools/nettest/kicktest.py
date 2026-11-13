#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Kick and ban in a lab (dev.27): two fake clients for mods/tests/kick-test, and Kiln's ban commands through the
console control folder. Needs a lab with "labPort" in kiln/config/network.json and kick-test deployed.

    tools/nettest/kicktest.py --port <labPort> --lab ~/srv/enshrouded/labs/<lab>

Prints PASS/FAIL lines and exits non-zero on any FAIL.
"""
import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import nettest  # noqa: E402

FAILS = []


def check(name, ok, detail=""):
    print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + detail if detail else ''}", flush=True)
    if not ok:
        FAILS.append(name)


def console(lab, name, line, seconds=10):
    d = os.path.join(lab, "kiln", "control", "console")
    os.makedirs(d, exist_ok=True)
    result = os.path.join(d, name + ".result.json")
    if os.path.exists(result):
        os.remove(result)
    with open(os.path.join(d, name + ".cmd.tmp"), "w") as f:
        f.write(line)
    os.replace(os.path.join(d, name + ".cmd.tmp"), os.path.join(d, name + ".cmd"))
    end = time.time() + seconds
    while time.time() < end:
        if os.path.exists(result):
            time.sleep(0.2)
            with open(result) as f:
                return json.load(f)
        time.sleep(0.2)
    return None


def replies(doc):
    out = []
    for e in (doc or {}).get("results", []):
        out += [str(x) for x in e.get("output") or []] + ([e["error"]] if e.get("error") else [])
    return out or [json.dumps(doc)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--lab", required=True)
    a = ap.parse_args()

    # a Kiln client: handshake, then Kiln's REJECT with the mod's reason, then nothing
    c = nettest.Client(a.port, "kick-kiln")
    c.hello([], [])
    check("the Kiln client's handshake", c.wait(lambda: c.welcome is not None, 15))
    check("the Kiln client gets Kiln's REJECT with the kick's reason", c.wait(lambda: c.reject is not None, 20),
          repr(c.reject))
    check("the reason is the one Network.Kick was given", c.reject is not None and c.reject[0] == "kick-test: bye, Kiln client")

    # a vanilla client (no Kiln frames): no REJECT, the server drops it
    v = nettest.Client(a.port, "kick-vanilla")
    v.send(nettest.LAB_VANILLA)
    time.sleep(0.3)
    v.send(nettest.LAB_READY)  # its Gameplay Ready: Kiln.Network judges it after vanillaHoldSeconds
    v.pump(15)
    check("a vanilla client gets no REJECT frame", v.reject is None)

    # the ban list through Kiln's console commands
    steam = "76561190000000123"
    r = console(a.lab, "ban1", f"ban {steam} 7d kicktest ban")
    check("/ban answers", r is not None and any("banned" in x for x in replies(r)), " | ".join(replies(r)) if r else "no result")
    r = console(a.lab, "bans1", "bans")
    check("/bans lists it with the reason and the end", r is not None and any(steam in x and "kicktest ban" in x and "until" in x for x in replies(r)),
          " | ".join(replies(r)) if r else "no result")
    path = os.path.join(a.lab, "kiln", "data", "kiln-players", "bans.json")
    data = json.load(open(path)) if os.path.exists(path) else {}
    check("kiln/data/kiln-players/bans.json holds it", any(str(b.get("steamId")) == steam for b in data.get("bans", [])), json.dumps(data)[:300])
    r = console(a.lab, "unban1", f"unban {steam}")
    check("/unban answers", r is not None and any("no longer banned" in x for x in replies(r)), " | ".join(replies(r)) if r else "no result")
    r = console(a.lab, "unban2", f"unban {steam}")
    check("/unban again: not on the list", r is not None and any("not on" in x for x in replies(r)), " | ".join(replies(r)) if r else "no result")
    r = console(a.lab, "bans2", "bans")
    check("/bans: empty", r is not None and any("empty" in x for x in replies(r)), " | ".join(replies(r)) if r else "no result")
    r = console(a.lab, "kick1", "kick nobody-here")
    check("/kick of a name that is not in game is refused", r is not None and any("no player" in x for x in replies(r)), " | ".join(replies(r)) if r else "no result")
    print(f"kicktest: {'FAILED ' + ', '.join(FAILS) if FAILS else 'all passed'}")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
