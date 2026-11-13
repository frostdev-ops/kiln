#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The channel kiln:instance without a player: fake Kiln clients on kiln.net's lab transport (tools/nettest).

A lab peer has no player and no inventory, so it tells the server which holder entity it stands for (a chest of the lab's
world, as if that chest were its inventory) and where it stands (LabView); from there on the server's own passes decide
what it may see and send it, as they do for a player's game. The mod instance-sync-test changes the items when asked over
its control channel. Needs a lab made from a world with containers, "labPort" in kiln/config/network.json, and the mod
deployed:

    tools/lab/lab.sh new dev27-affix 27360 --save ~/Work/ew-lab/server/savegame
    echo '{ "labPort": 27361 }' > ~/srv/enshrouded/labs/dev27-affix/kiln/config/network.json
    KILN_OUT=... tools/deploy-server.sh ~/srv/enshrouded/labs/dev27-affix --policy any --no-build   (with the mod in $KILN_OUT/mods)
    labstart dev27-affix
    mods/tests/instance-sync-test/syncnet.py --port 27361
    tools/lab/lab.sh stop dev27-affix; labstart dev27-affix
    mods/tests/instance-sync-test/syncnet.py --port 27361 --phase restart

first (the default): the holder, nobody else, nearby, moves, removals, losses, sizes, a later join, clients without the channel.
restart: after a stop and a start: the values the first phase left on two swords are back on them (the store matched
them) and reach the holder's game in its first update, with nothing set in this boot.

Prints PASS/FAIL lines and exits non-zero on any FAIL.
"""
import argparse
import json
import os
import struct
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "tools", "nettest"))
import nettest as nt  # noqa: E402

CH, CTL = "kiln:instance", "instance-sync-test:ctl"
UPDATE, LABVIEW = 0, 1          # kiln:instance v1 (InstanceSync.cs)
REQ, REPLY = 0, 1               # the mod's control channel, in registration order
MOD = "instance-sync-test"
BUDGET = 96 * 1024              # InstanceSyncServer.UpdateBudgetBytes


def decode(body):
    """One kiln:instance update: (reset, [gone entity], [item dict])."""
    r = nt.R(body)
    version = r.u8()
    if version != 1:
        raise ValueError(f"update version {version}")
    reset = r.boolean()
    gone = [r.u32() for _ in range(r.varu())]
    items = []
    for _ in range(r.varu()):
        entity, item, holder = r.u32(), r.u32(), r.u32()
        z = r.varu()
        slot = (z >> 1) ^ -(z & 1)
        values = []
        for _ in range(r.varu()):
            mod, key = r.string(), r.string()
            values.append((mod, key, r.string() if r.boolean() else None))
        items.append(dict(entity=entity, item=item, holder=holder, slot=slot, values=values))
    return reset, gone, items


class C(nt.Client):
    """A fake game: keeps the copies the server sends, as Kiln's client store does."""

    def __init__(self, port, name, verbose=False):
        super().__init__(port, name, verbose)
        self.items = {}      # entity -> dict(item, holder, slot, values {(mod, key): parsed JSON})
        self.updates = []    # (time, bytes, reset, gone, items)
        self.raw = {}        # (entity, mod, key) -> the JSON text as sent

    def set_table(self, table):
        self.table = table
        self.channels = {n: i for i, n, v, _ in table if n in (CH, CTL) and v == "1"}

    def join(self, channels):
        self.hello([], [(c, "1", False) for c in channels])
        return self.wait(lambda: self.welcome is not None, 15)

    def ctl(self, op, item=0, key=None, count=0, size=0, seconds=20.0):
        self.data(CTL, REQ, json.dumps({"Op": op, "Item": item, "Key": key, "Count": count, "Size": size}).encode())
        b = self.take(CTL, REPLY, seconds)
        return None if b is None else json.loads(b.rstrip(b"\0").decode())

    def view(self, entity, at=None):
        w = nt.W().u32(entity).boolean(at is not None)
        for v in (at or (0.0, 0.0, 0.0)):
            w.raw(struct.pack("<d", v))
        self.data(CH, LABVIEW, w.b)

    def pump(self, seconds):
        super().pump(seconds)
        cid, keep = self.channels.get(CH), []
        for m in self.messages:
            if cid is not None and m[0] == cid and m[1] == UPDATE:
                self.apply(m[2])
            else:
                keep.append(m)
        self.messages = keep

    def apply(self, body):
        reset, gone, items = decode(body)
        self.updates.append((time.time(), len(body), reset, gone, items))
        if reset:
            self.items.clear()
        for e in gone:
            self.items.pop(e, None)
        for i in items:
            have = self.items.setdefault(i["entity"], dict(item=i["item"], holder=i["holder"], slot=i["slot"], values={}))
            have.update(item=i["item"], holder=i["holder"], slot=i["slot"])
            for mod, key, text in i["values"]:
                if text is None:
                    have["values"].pop((mod, key), None)
                else:
                    have["values"][(mod, key)] = json.loads(text)
                    self.raw[(i["entity"], mod, key)] = text
            if not have["values"]:
                del self.items[i["entity"]]

    def value(self, entity, key):
        return self.items.get(entity, {}).get("values", {}).get((MOD, key))

    def until(self, pred, seconds=4.0):
        """Seconds until pred() held, or None."""
        t0 = time.time()
        return time.time() - t0 if self.wait(pred, seconds) else None

    def saw(self, key):
        return any(k == key for _, _, _, _, items in self.updates for i in items for _, k, _ in i["values"])


class Suite:
    def __init__(self):
        self.passed = self.failed = 0

    def check(self, name, ok, detail=""):
        print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + str(detail)[:400] if detail != '' else ''}", flush=True)
        if ok:
            self.passed += 1
        else:
            self.failed += 1
        return ok


def restart(a, s):
    """After a stop and a start: the two swords the first phase left in chest A carry their values again."""
    p = C(a.port, "sync-restart", a.verbose)
    if not s.check("handshake after the restart", p.join([CH, CTL]), p.reject):
        return 1
    rows = None
    for _ in range(30):  # the store looks for a chest's records as it streams in: within a few seconds
        r = p.ctl("list")
        rows = json.loads(r["Data"]) if r and r["Ok"] else None
        if rows and len([x for x in rows if x["Held"] is not None]) >= 2:
            break
        p.pump(1.0)
    held = sorted((x for x in (rows or []) if x["Held"] is not None), key=lambda x: x["Held"])
    s.check("the store put the values back on two swords (40 and 41, each with its 40 KB text)",
            [(x["Held"], x["HeldText"]) for x in held] == [(40, 40000), (41, 40000)], [(x["Entity"], x["Held"], x["HeldText"], x["Seen"]) for x in held])
    if len(held) != 2:
        return 1
    holder = held[0]["Holder"]
    s.check("both in the chest they were left in, and the second still has its Nearby value too",
            held[1]["Holder"] == holder and held[1]["Seen"] == 42 and held[1]["SeenText"] == 40000, held)
    p.view(holder, None)
    t = p.until(lambda: all((p.value(x["Entity"], "held") or {}).get("Count") == x["Held"] for x in held)
                and (p.value(held[1]["Entity"], "seen") or {}).get("Count") == 42, 8)
    s.check("the holder's game gets them in its first updates, with nothing set in this boot", t is not None and p.updates[0][2],
            {e: {k: v.get("Count") for (_, k), v in i["values"].items()} for e, i in p.items.items()})
    s.check("under the item entities of this boot, at the slots the server reads now",
            all(p.items.get(x["Entity"], {}).get("slot") == x["Flat"] and p.items.get(x["Entity"], {}).get("holder") == holder for x in held))
    s.check("whole", (p.value(held[1]["Entity"], "seen") or {}).get("Text") == "b" * 40000)
    s.check("and the key without Sync stayed on the server through the restart", not p.saw("secret"))
    left = p.ctl("cleanup", seconds=60)
    t = p.until(lambda: not p.items, 8)
    s.check("taking the swords out of the world empties the holder's game", t is not None, (left, list(p.items)))
    print(f"syncnet restart: {s.passed} PASS, {s.failed} FAIL", flush=True)
    return 1 if s.failed else 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--verbose", action="store_true")
    ap.add_argument("--phase", default="first", choices=["first", "restart"])
    a = ap.parse_args()
    s = Suite()
    if a.phase == "restart":
        return restart(a, s)

    p1 = C(a.port, "sync-a", a.verbose)
    if not s.check("handshake of a Kiln client with kiln:instance and the control channel", p1.join([CH, CTL]), p1.reject):
        return 1
    entry = next((t for t in p1.table if t[1] == CH), None)
    s.check("the server has channel kiln:instance v1, not required", entry is not None and entry[2] == "1" and not entry[3], entry)

    caps = p1.ctl("caps")
    caps = json.loads(caps["Data"]) if caps and caps["Ok"] else None
    s.check("capabilities saves.instance_data and saves.instance_data.sync are available", bool(caps and caps["Sync"] and caps["Data"]), caps)

    left = p1.ctl("cleanup", seconds=60)
    print(f"INFO {left and left['Data']}", flush=True)
    p1.pump(1.0)
    setup = p1.ctl("setup", seconds=60)
    if not s.check("setup: two chests of the world, two swords in A and one in B", bool(setup and setup["Ok"]), setup):
        return 1
    w = json.loads(setup["Data"])
    A, B, pos_a, pos_b = w["A"], w["B"], w["PosA"], w["PosB"]
    it = w["Items"]
    far = [pos_a[0] + 500.0, pos_a[1], pos_a[2]]  # where the holder of chest B "stands": near nothing
    print(f"INFO chest A {A} at {pos_a}, chest B {B} at {pos_b}; items {it}", flush=True)
    s.check("the swords sit where the mod says", it[0]["Holder"] == A and it[1]["Holder"] == A and it[2]["Holder"] == B)

    # ---- the holder ----
    p1.view(A, pos_a)
    s.check("the first update to a game starts its copies over (reset), with nothing in it yet",
            p1.until(lambda: len(p1.updates) > 0) is not None and p1.updates[0][2] and not p1.updates[0][4], p1.updates[:1])
    p1.ctl("set", 0, "held", 5)
    t = p1.until(lambda: p1.value(it[0]["Entity"], "held") is not None)
    s.check("a value set on the holder's item arrives", t is not None and p1.value(it[0]["Entity"], "held")["Count"] == 5,
            f"{p1.items.get(it[0]['Entity'])} after {t and round(t * 1000)} ms")
    got = p1.items.get(it[0]["Entity"], {})
    s.check("with the item's type, holder and flat slot as the server reads them",
            (got.get("item"), got.get("holder"), got.get("slot")) == (it[0]["Item"], A, it[0]["Flat"]), got)
    s.check("the value is the server's JSON", p1.raw.get((it[0]["Entity"], MOD, "held")) == '{"Count":5,"Text":null}', p1.raw)
    n = len(p1.updates)
    p1.ctl("set", 0, "secret", 9)
    p1.pump(1.5)
    s.check("a key without Sync is never sent, and causes no update", not p1.saw("secret") and len(p1.updates) == n, p1.updates[n:])
    p1.ctl("edit", 0, "held", 6)
    t = p1.until(lambda: (p1.value(it[0]["Entity"], "held") or {}).get("Count") == 6)
    s.check("a change of the live object (no Set) arrives within a second", t is not None and t < 1.0, f"{t and round(t * 1000)} ms")
    n = len(p1.updates)
    p1.pump(1.5)
    s.check("nothing is sent while nothing changes", len(p1.updates) == n, p1.updates[n:])

    # ---- nobody else ----
    p2 = C(a.port, "sync-b", a.verbose)
    p2.join([CH, CTL])
    p2.view(B, far)
    s.check("a second game (the holder of chest B) gets its own reset", p2.until(lambda: len(p2.updates) > 0) is not None and p2.updates[0][2])
    p2.ctl("set", 2, "held", 3)
    t = p2.until(lambda: p2.value(it[2]["Entity"], "held") is not None)
    s.check("B's holder gets the value of B's sword", t is not None and p2.value(it[2]["Entity"], "held")["Count"] == 3)
    p1.pump(1.0)
    s.check("A's holder gets nothing of B's sword, and B's holder nothing of A's",
            it[2]["Entity"] not in p1.items and it[0]["Entity"] not in p2.items, (list(p1.items), list(p2.items)))

    # ---- nearby ----
    p3 = C(a.port, "sync-near", a.verbose)
    p3.join([CH, CTL])
    p3.view(999, [pos_a[0] + 3, pos_a[1], pos_a[2]])  # stands 3 m from chest A, holds nothing
    p3.until(lambda: len(p3.updates) > 0)
    p3.ctl("set", 1, "seen", 7)                       # Sync.Nearby, 10 m
    t = p3.until(lambda: p3.value(it[1]["Entity"], "seen") is not None)
    s.check("a Nearby value reaches a game standing 3 m from the holder", t is not None and p3.value(it[1]["Entity"], "seen")["Count"] == 7, p3.items)
    s.check("the holder gets it too", p1.until(lambda: p1.value(it[1]["Entity"], "seen") is not None) is not None)
    s.check("the game nearby gets no Holder value of that chest's items", it[0]["Entity"] not in p3.items and not p3.saw("held"), p3.items)
    p2.pump(1.0)
    s.check("a game far away gets nothing of it", it[1]["Entity"] not in p2.items and not p2.saw("seen"), p2.items)
    p3.view(999, [pos_a[0] + 30, pos_a[1], pos_a[2]])
    t = p3.until(lambda: it[1]["Entity"] not in p3.items)
    s.check("walking out of range takes the copy away (gone)", t is not None and any(it[1]["Entity"] in g for _, _, _, g, _ in p3.updates), p3.updates[-1:])
    p3.view(999, [pos_a[0] + 9, pos_a[1], pos_a[2]])
    s.check("walking back in brings it again", p3.until(lambda: p3.value(it[1]["Entity"], "seen") is not None) is not None)

    # ---- moves, removals, losses ----
    r = p1.ctl("move", 0)
    moved = json.loads(r["Data"]) if r and r["Ok"] else None
    s.check("Items.Move took sword 0 from chest A to chest B (the same item entity)",
            bool(moved and moved["Holder"] == B and moved["Entity"] == it[0]["Entity"]), r)
    t1 = p1.until(lambda: it[0]["Entity"] not in p1.items)
    t2 = p2.until(lambda: p2.value(it[0]["Entity"], "held") is not None)
    s.check("the old holder's copy goes", t1 is not None, p1.items)
    s.check("the new holder gets the value with the new place", t2 is not None and moved is not None and
            (p2.items[it[0]["Entity"]]["holder"], p2.items[it[0]["Entity"]]["slot"], p2.value(it[0]["Entity"], "held")["Count"]) == (B, moved["Flat"], 6),
            p2.items.get(it[0]["Entity"]))
    p2.ctl("set", 2, "seen", 4)
    p2.until(lambda: p2.value(it[2]["Entity"], "seen") is not None)
    p2.ctl("remove", 2, "held")
    t = p2.until(lambda: p2.value(it[2]["Entity"], "held") is None)
    s.check("a removed value is removed on the holder's game; the item's other value stays",
            t is not None and p2.value(it[2]["Entity"], "seen") == {"Count": 4, "Text": None}, p2.items.get(it[2]["Entity"]))
    s.check("it went as a removal of that key, not as the item gone",
            any(k == "held" and text is None for _, _, _, _, items in p2.updates for i in items for _, k, text in i["values"]))
    r = p2.ctl("destroy", 2)
    t = p2.until(lambda: it[2]["Entity"] not in p2.items)
    s.check("a destroyed item leaves the holder's game", bool(r and r["Ok"]) and t is not None, r)

    # ---- size: more than one update holds ----
    r = p1.ctl("move", 0)  # sword 0 back into chest A
    s.check("sword 0 is back in chest A", bool(r and r["Ok"] and json.loads(r["Data"])["Holder"] == A), r)
    p1.until(lambda: p1.value(it[0]["Entity"], "held") is not None)
    n = len(p1.updates)
    p1.ctl("set", 0, "held", 40, 40000)
    p1.ctl("set", 1, "held", 41, 40000)
    p1.ctl("set", 1, "seen", 42, 40000)
    def big():
        return all(len((p1.value(it[i]["Entity"], k) or {}).get("Text") or "") == 40000 for i, k in ((0, "held"), (1, "held"), (1, "seen")))
    t = p1.until(big, 8)
    sizes = [b for _, b, _, _, _ in p1.updates[n:]]
    s.check("three values of 40 KB (120 KB, more than one update's 96 KB) all arrive whole", t is not None, f"updates of {sizes} bytes")
    s.check("over more than one update, none above the budget", len(sizes) >= 2 and all(b <= BUDGET + 2048 for b in sizes), sizes)
    s.check("and nothing was cut", (p1.value(it[1]["Entity"], "seen") or {}).get("Text") == "b" * 40000 and p1.value(it[1]["Entity"], "seen")["Count"] == 42)

    # ---- a new connection of the same holder ----
    p4 = C(a.port, "sync-a-again", a.verbose)
    p4.join([CH, CTL])
    p4.view(A, pos_a)
    t = p4.until(lambda: len(p4.items) == 2 and len(p4.items.get(it[1]["Entity"], {}).get("values", {})) == 2
                 and (MOD, "held") in p4.items.get(it[0]["Entity"], {}).get("values", {}), 8)
    s.check("a game that joins later gets the whole set, starting with a reset", t is not None and p4.updates[0][2],
            {e: sorted(k for _, k in v["values"]) for e, v in p4.items.items()})
    s.check("still without the key that has no Sync", not p4.saw("secret"))

    # ---- games that must notice nothing ----
    p5 = C(a.port, "sync-old", a.verbose)  # a Kiln client without the channel (an older Kiln, or no mod that reads)
    p5.join([CTL])
    p5.pump(2.0)
    s.check("a Kiln client without the channel gets no message of it", not p5.updates and not p5.messages, p5.messages[:2])
    p6 = C(a.port, "sync-vanilla", a.verbose)
    p6.send(nt.LAB_VANILLA)
    p6.pump(2.0)
    s.check("a client without Kiln gets no frame at all", not p6.frame_len and not p6.messages, p6.frame_len)

    print(f"syncnet: {s.passed} PASS, {s.failed} FAIL", flush=True)
    return 1 if s.failed else 0


if __name__ == "__main__":
    sys.exit(main())
