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

first (the default): the holder, nobody else, nearby, moves, removals, losses, sizes, a later join, clients without the channel,
an update lost on the way (noticed by the next, and the game starts over), a game that acknowledges nothing (at most eight
updates, a removal still reaches it, then a start over after ten seconds), a game that reports a loss every second (started
over at a falling rate).
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
UPDATE, LABVIEW, ACK = 0, 1, 2  # kiln:instance v1 (InstanceSync.cs)
REQ, REPLY = 0, 1               # the mod's control channel, in registration order
MOD = "instance-sync-test"
BUDGET = 96 * 1024              # InstanceSyncServer.UpdateBudgetBytes: the whole encoded update
IN_FLIGHT = 8                   # InstanceSyncServer.MaxInFlight
ACK_TIMEOUT = 10.0              # InstanceSyncServer.AckTimeoutMs


def decode(body):
    """One kiln:instance update: (reset, seq, prev, [gone entity], [item dict])."""
    r = nt.R(body)
    version = r.u8()
    if version != 1:
        raise ValueError(f"update version {version}")
    reset = r.boolean()
    seq, prev = r.u32(), r.u32()
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
    return reset, seq, prev, gone, items


class C(nt.Client):
    """A fake game: keeps the copies the server sends, as Kiln's client store does."""

    def __init__(self, port, name, verbose=False):
        super().__init__(port, name, verbose)
        self.items = {}      # entity -> dict(item, holder, slot, values {(mod, key): parsed JSON})
        self.updates = []    # (time, bytes, reset, gone, items) of the updates taken
        self.raw = {}        # (entity, mod, key) -> the JSON text as sent
        self.last, self.chain, self.lost_said = 0, False, False   # as Kiln's client store: the update taken last
        self.drop = 0        # the next this many updates are lost on the way (never seen, never acknowledged)
        self.ack = True      # False: this game acknowledges nothing
        self.received = 0    # updates that arrived (taken or not)
        self.skipped = 0     # updates that arrived but did not follow the one taken last
        self.losses = []     # the "lost" reports sent: the update taken last
        self.last_reset = 0  # the number of the last reset taken

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

    def send_ack(self, seq, lost):
        self.data(CH, ACK, nt.W().u32(seq).boolean(lost).b)

    def apply(self, body):
        reset, seq, prev, gone, items = decode(body)
        if self.drop > 0:
            self.drop -= 1
            return
        self.received += 1
        if not reset and (not self.chain or prev != self.last):
            # an update was lost before this one: not taken; said once, then the server starts this game over
            self.skipped += 1
            if not self.lost_said:
                self.lost_said = True
                self.losses.append(self.last)
                if self.ack:
                    self.send_ack(self.last, True)
            return
        self.last, self.chain, self.lost_said = seq, True, False
        if reset:
            self.last_reset = seq
        if self.ack:
            self.send_ack(seq, False)
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
    # The server's passes are its only sender, and they run on its steps: a server that does not step (an autosave on a
    # loaded machine held one for 9.3 s, 31 ms after the return, in the dev.27 rehearsal) sends nothing. A control round
    # trip proves it stepped after the view. From then on the value comes at the next pass (0.25 s), or, if the network
    # lost the update that carried it and nothing follows, at the start over ACK_TIMEOUT after it was sent; plus 2 s for
    # a loaded lab (InstanceSyncTests.WalkingBackInWithinAPassBringsTheValueAtTheNextPass and ...AfterAStall...).
    n = len(p3.updates)
    p3.ctl("caps")
    t = p3.until(lambda: p3.value(it[1]["Entity"], "seen") is not None, ACK_TIMEOUT + 2.0)
    s.check(f"walking back in brings it again (at the next pass once the server steps; {ACK_TIMEOUT + 2.0:.0f} s for an update lost on the way)",
            t is not None, f"after {t and round(t * 1000)} ms, {'in a reset' if any(u[2] for u in p3.updates[n:]) else 'as a difference'}; {p3.items}")

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
    s.check("over more than one update, none above the budget (everything in an update counted)", len(sizes) >= 2 and all(b <= BUDGET for b in sizes), sizes)
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

    # ---- a holder that walks: "nearby" is where it stands now ----
    r = p1.ctl("npc", seconds=30)
    npc = json.loads(r["Data"]) if r and r["Ok"] else None
    if s.check("a stand-in NPC (a player-template entity) 20 m from chest A carries a sword", bool(npc and npc["Pos"] and npc["Holder"] == npc["Npc"]), r):
        at = npc["Pos"]
        p8 = C(a.port, "sync-walker", a.verbose)
        p8.join([CH, CTL])
        p8.view(999, [at[0] + 3, at[1], at[2]])
        p8.until(lambda: p8.received > 0)
        p8.ctl("set", 3, "seen", 9)                   # Sync.Nearby, 10 m
        t = p8.until(lambda: p8.value(npc["Entity"], "seen") is not None)
        s.check("a game 3 m from the NPC gets the Nearby value of the sword it carries", t is not None, p8.items)
        w = p8.ctl("walk", 3, seconds=30)
        moved = json.loads(w["Data"]) if w and w["Ok"] else None
        dist = (sum((x - y) ** 2 for x, y in zip(moved["Pos"], at)) ** 0.5) if moved and moved["Pos"] else 0
        s.check("the NPC walks (Teleport.To 60 m on) with the sword in the same slot of its inventory",
                bool(moved and dist > 40 and moved["Holder"] == npc["Holder"] and moved["Flat"] == npc["Flat"]), (w, round(dist, 1)))
        t = p8.until(lambda: npc["Entity"] not in p8.items, 3)
        s.check("the game at its old place loses the copy: where the holder stands now counts, not where it took the item",
                t is not None, f"{p8.items.get(npc['Entity'])} after {t and round(t * 1000)} ms")
        if moved and moved["Pos"]:
            p8.view(999, [moved["Pos"][0] + 3, moved["Pos"][1], moved["Pos"][2]])
            t = p8.until(lambda: p8.value(npc["Entity"], "seen") is not None)
            s.check("a game beside where it stands now gets it", t is not None, p8.items)
        p1.ctl("npc-gone", seconds=30)
        p1.pump(0.5)
        s.check("the other games got nothing of the NPC's sword (no Holder value travels to anyone: an NPC is no player)",
                all(npc["Entity"] not in c.items for c in (p1, p2, p4)) and not any(k == "held" and i["entity"] == npc["Entity"]
                    for c in (p1, p2, p3, p4, p8) for _, _, _, _, items in c.updates for i in items for _, k, _ in i["values"]))

    # ---- an update lost on the way ----
    p1.ctl("set", 0, "held", 50)
    p1.until(lambda: (p1.value(it[0]["Entity"], "held") or {}).get("Count") == 50)
    p1.drop = 1                                       # the next update is lost between the server and this game
    resets = sum(1 for u in p1.updates if u[2])
    p1.ctl("set", 0, "held", 51)                      # lost
    p1.pump(0.6)
    p1.ctl("set", 1, "held", 52)                      # arrives, but does not follow what the game took
    t = p1.until(lambda: (p1.value(it[0]["Entity"], "held") or {}).get("Count") == 51
                 and (p1.value(it[1]["Entity"], "held") or {}).get("Count") == 52, 6)
    s.check("an update lost on the way is noticed by the next one, which is not taken", p1.skipped >= 1 and len(p1.losses) == 1,
            (p1.skipped, p1.losses))
    s.check("the server starts that game over: a reset with the whole set, the lost value in it, within a second or two",
            t is not None and sum(1 for u in p1.updates if u[2]) == resets + 1, f"{t and round(t * 1000)} ms")
    s.check("after the reset the game holds everything it may see again",
            len(p1.items.get(it[1]["Entity"], {}).get("values", {})) == 2 and (p1.value(it[1]["Entity"], "seen") or {}).get("Count") == 42,
            {e: sorted(k for _, k in v["values"]) for e, v in p1.items.items()})

    # ---- a game that acknowledges nothing ----
    p7 = C(a.port, "sync-mute", a.verbose)
    p7.join([CH, CTL])
    p7.ack = False
    p7.view(B, far)                                   # the holder of chest B (sword 0 is in A again; B holds nothing of the test's)
    p7.until(lambda: p7.received > 0)
    r = p7.ctl("move", 0)                             # sword 0 into chest B: its values reach B's holder
    p7.until(lambda: p7.value(it[0]["Entity"], "held") is not None, 4)
    for n in range(60, 72):                           # twelve changes, each in its own pass
        p7.ctl("edit", 0, "held", n)
        p7.pump(0.35)
    got = p7.received
    s.check(f"a game that acknowledges nothing gets at most {IN_FLIGHT} updates, then nothing new while none is acknowledged",
            got <= IN_FLIGHT, f"{got} updates")
    p7.ctl("remove", 0, "held")                       # its only value: the player may no longer see the sword's data
    t = p7.until(lambda: it[0]["Entity"] not in p7.items, 3)
    s.check(f"a removal still reaches it with {IN_FLIGHT} updates unacknowledged (removals do not wait behind them)",
            t is not None and p7.received == got + 1, f"after {t and round(t * 1000)} ms; {p7.received} updates")
    p7.ack = True
    t = p7.until(lambda: p7.updates and p7.updates[-1][2], ACK_TIMEOUT + 6)
    s.check("ten seconds after its oldest update it is started over with a reset, and is current again",
            t is not None and it[0]["Entity"] not in p7.items, f"after {t and round(t, 1)} s more; {p7.items}")
    r = p1.ctl("move", 0)                             # sword 0 back into chest A
    p1.ctl("set", 0, "held", 40, 40000)               # what the restart phase looks for, as before these checks
    p1.ctl("set", 1, "held", 41, 40000)
    p1.until(lambda: (p1.value(it[0]["Entity"], "held") or {}).get("Count") == 40
             and (p1.value(it[1]["Entity"], "held") or {}).get("Count") == 41, 6)

    # ---- a game that reports a loss every second ----
    p9 = C(a.port, "sync-liar", a.verbose)
    p9.join([CH, CTL])
    p9.view(A, pos_a)
    p9.until(lambda: len(p9.items) == 2 and len(p9.items.get(it[1]["Entity"], {}).get("values", {})) == 2, 8)
    first = len(p9.updates)
    t0 = time.time()
    while time.time() - t0 < 16:                      # "lost one after your last reset", once a second, for 16 seconds
        p9.send_ack(p9.last_reset, True)
        p9.pump(1.0)
    starts = [round(u[0] - t0, 1) for u in p9.updates[first:] if u[2]]
    gaps = [round(b - a, 1) for a, b in zip(starts, starts[1:])]
    s.check("a game that reports a loss every second is started over at a falling rate (1, 2, 4, 8 s apart), not every second",
            2 <= len(starts) <= 5 and all(g2 >= 1.6 * g1 for g1, g2 in zip(gaps, gaps[1:])), f"resets at {starts} s")
    t = p9.until(lambda: len(p9.items) == 2 and len(p9.items.get(it[1]["Entity"], {}).get("values", {})) == 2, 4)
    s.check("and each start over still gives it the whole set it may see", t is not None, {e: sorted(k for _, k in v["values"]) for e, v in p9.items.items()})

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
