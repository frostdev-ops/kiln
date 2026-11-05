#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The shadow table's client sync, without a client: a fake Kiln client on kiln.net's lab transport (tools/nettest).

The real order at a join: the player is in game and the table loads, THEN the client's Kiln handshake finishes. What the server
sent at load went nowhere, and a reload that changed nothing sent nothing (seen: a player's screen stayed Locked with 5 shadow
nodes on the server). So this connects after the table loaded, and must get the table without anything changing.

    shadownet.py --port P     # lab with skills-test and {"labPort": P} in kiln/config/network.json, while skills-test logs "waiting for shadownet.py"
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "tools", "nettest"))
import nettest as nt  # noqa: E402

CH, CTL = "kiln:skills", "skills-test:ctl"
REQUEST, STATE, SHADOW = range(3)  # registration order in TreeNet.cs
CTL_REQ, CTL_REPLY = range(2)


class C(nt.Client):
    def set_table(self, table):
        self.table = table
        self.channels = {n: i for i, n, v, _ in table if (n == CH or n == CTL) and v == "1"}

    def get(self, ch, msg, seconds=8.0):
        b = self.take(ch, msg, seconds)
        return None if b is None else json.loads(b.rstrip(b"\0").decode())

    def ctl(self, op, text=""):
        self.data(CTL, CTL_REQ, json.dumps({"Op": op, "Text": text}).encode())
        return self.get(CTL, CTL_REPLY, 10.0)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    a = ap.parse_args()
    failed = 0

    channels = [(CH, "1", False), (CTL, "1", False)]
    mods = nt.with_required(a.port, [("skills-test", "0.1.0", 2), ("skilltreesample", "1.0.0", 2)], channels)
    c = C(a.port, "shadownet")
    c.hello(mods, channels)
    if not c.wait(lambda: c.welcome is not None, 10):
        print("FAIL no WELCOME: is the lab up with labPort?")
        return 2

    def check(ok, what):
        nonlocal failed
        failed += 0 if ok else 1
        line = f"{'PASS' if ok else 'FAIL'} {what}"
        print(line, flush=True)
        c.ctl("check", line)

    # the peer is ready, but it isn't this player's connection yet: nothing may reach it
    check(c.get(CH, SHADOW, 1.0) is None, "a peer that is nobody's connection gets nothing")
    bound = c.ctl("bind")
    if not bound:
        print("FAIL no answer on skills-test:ctl")
        return 2
    # the player's table loaded long ago and did not change since: the peer gets it now
    m = c.get(CH, SHADOW, 5.0)
    check(m is not None and len(m["Levels"]) == 2 and sorted(m["Levels"].values()) == [1, 2],
          f"the client gets the table once its peer can take it, with no change on the server: {m}")
    # it goes once
    check(c.get(CH, SHADOW, 1.0) is None, "and only once")
    # every change follows, also to nothing changed before
    c.ctl("change")
    m2 = c.get(CH, SHADOW, 5.0)
    check(m2 is not None and len(m2["Levels"]) == 1, f"a change on the server reaches it: {m2}")
    c.ctl("done")
    print(f"shadownet: {'all passed' if failed == 0 else f'{failed} failed'}")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
