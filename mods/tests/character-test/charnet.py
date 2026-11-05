#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The channel kiln:character without a client: a fake Kiln client on kiln.net's lab transport (tools/nettest).

A lab peer has no player, so it tells the server which character it pretends to be (CharLabJoin), and the rest runs the code a
real client's messages run. Its "sidecar" is a JSON file. Needs the mod character-test and "labPort" in kiln/config/network.json.

    charnet.py --port P --lab LAB --sidecar FILE --phase first|restart|other|back|hosted1|hosted2

first:   a new character on this server; what it sends back; quests don't travel; what the server refuses; the import setting
restart: after a restart: the server's copy, and a client that lost its file
other:   another server (another world store): the character brings its talents, its quests stay behind
back:    the first server again: the newer talents come along, the quests that stayed there are still there
hosted1: the local player of a hosted game: the server reads and writes the sidecar file itself (kiln/data/characters/)
hosted2: after a wiped world store: the file brings the talents back; a broken file falls back on its older copy
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "tools", "nettest"))
import nettest as nt  # noqa: E402

CH, CTL = "kiln:character", "character-test:ctl"
HELLO, OFFER, RESULT, UPDATE, ACK, LABJOIN, LABLEAVE = range(7)  # registration order in CharacterSync.cs
REQ, REPLY = range(2)
A = "aaaaaaaa-0000-4000-8000-00000000000a"
ACCOUNT = 0x1111222233335555
TREE = "talents:character-test:tree"


class C(nt.Client):
    def set_table(self, table):
        self.table = table
        self.channels = {n: i for i, n, v, _ in table if (n == CH or n == CTL) and v == "1"}

    def js(self, ch, msg, obj):
        self.data(ch, msg, json.dumps(obj).encode())

    def get(self, ch, msg, seconds=8.0):
        b = self.take(ch, msg, seconds)
        return None if b is None else json.loads(b.rstrip(b"\0").decode())

    def ctl(self, op, character=A, name=None, text=None, seconds=10.0):
        self.js(CTL, REQ, {"Op": op, "Character": character, "Account": ACCOUNT, "Name": name, "Text": text})
        return self.get(CTL, REPLY, seconds)

    def join(self, character=A):
        self.js(CH, LABJOIN, {"Character": character, "Account": ACCOUNT})
        return self.get(CH, HELLO)

    def drain(self):
        self.pump(0.3)
        self.messages.clear()

    def leave(self, character=A):
        self.js(CH, LABLEAVE, {"Character": character})
        self.pump(0.3)

    def offer(self, character, revision, mods):
        self.js(CH, OFFER, {"Character": character, "Revision": revision, "Mods": mods})
        return self.get(CH, RESULT)


class Suite:
    def __init__(self):
        self.passed = self.failed = 0

    def check(self, name, ok, detail=""):
        print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + str(detail)[:300] if detail != '' else ''}", flush=True)
        if ok:
            self.passed += 1
        else:
            self.failed += 1


def load(path):
    return json.load(open(path)) if os.path.exists(path) else {"revision": 0, "mods": {}}


def save(path, side):
    json.dump(side, open(path, "w"), indent=1)


def apply(side, u):
    for mod, keys in u["Changes"].items():
        d = side["mods"].setdefault(mod, {})
        for k, v in keys.items():
            if v is None:
                d.pop(k, None)
            else:
                d[k] = v
    side["revision"] = u["Revision"]


def wait_update(c, side, path, seconds=10.0):
    u = c.get(CH, UPDATE, seconds)
    if u:
        apply(side, u)
        save(path, side)
        c.js(CH, ACK, {"Character": u["Character"], "Revision": u["Revision"]})
        c.pump(0.2)
    return u


def start(port, verbose):
    c = C(port, f"charnet-{os.getpid()}", verbose)
    c.hello([], [(CH, "1", False), (CTL, "1", False)])
    if not c.wait(lambda: c.welcome is not None, 8):
        print("FAIL handshake", c.reject)
        sys.exit(1)
    return c


def first(c, s, a):
    side = load(a.sidecar)
    entry = next((t for t in c.table if t[1] == CH), None)
    s.check("channel kiln:character v1 is there and not required", entry is not None and not entry[3], entry)
    h = c.join()
    s.check("join: the server says hello", h is not None and h["Imports"] and h["Revision"] == 0 and h["World"], h)
    r = c.offer(A, 0, {})
    s.check("an empty client on an empty server: nothing to take", r is not None and not r["Accepted"], r)
    s.check("no update without a change", c.get(CH, UPDATE, 0.5) is None)
    c.js(CH, OFFER, {"Character": A, "Revision": 50, "Mods": {"kiln-skills": {TREE: {"Levels": {"a": 3}}}}})
    s.check("a second offer in a session gets no answer", c.get(CH, RESULT, 0.6) is None)
    st = c.ctl("state")
    s.check("and changes nothing", st and json.loads(st["Data"])["Levels"] == {}, st)

    # talents go back at a world save
    u = c.ctl("unlock", name="a")
    s.check("a talent on the server", u and u["Ok"], u)
    c.ctl("note-set", text="in my bag")
    c.ctl("save")
    up = wait_update(c, side, a.sidecar)
    s.check("the save sends it back", up is not None and "kiln-skills" in up["Changes"], up)
    levels = (up or {}).get("Changes", {}).get("kiln-skills", {}).get(TREE, {}).get("Levels")
    s.check("with the node's level", levels == {"a": 1}, levels)
    s.check("a mod's carried data goes too", (up or {}).get("Changes", {}).get("character-test", {}).get("bag", {}).get("Text") == "in my bag", up)
    s.check("the revision grew", side["revision"] >= 1, side["revision"])
    c.ctl("save")
    s.check("no update when nothing changed", c.get(CH, UPDATE, 1.0) is None)

    # quests stay on the world
    q = c.ctl("quest-start")
    s.check("a quest starts for the character", q and q["Ok"], q)
    c.ctl("quest-signal", name="character-test.one")
    st = c.ctl("quest-status")
    s.check("its first step is done", st and st["Data"].endswith("/two"), st)
    c.ctl("save")
    u2 = c.get(CH, UPDATE, 1.0)
    s.check("a quest does not travel: no update, nothing of kiln-quests", u2 is None or "kiln-quests" not in u2["Changes"], u2)
    s.check("nothing of kiln-quests in the sidecar", "kiln-quests" not in side["mods"], list(side["mods"]))

    # a second character of the account: its own everything
    other = "bbbbbbbb-0000-4000-8000-00000000000b"
    st2 = c.ctl("state", character=other)
    s.check("a second character has no talents", st2 and json.loads(st2["Data"])["Levels"] == {}, st2)
    st3 = c.ctl("quest-status", character=other)
    s.check("a second character has no quest", st3 and st3["Data"].startswith("Available"), st3)

    # an offer for a character the session isn't
    r = c.offer(other, 99, {"kiln-skills": {}})
    s.check("an offer for another character is refused", r is not None and not r["Accepted"], r)
    c.leave()

    # what the server checks: a new character with a client-side file
    d = "dddddddd-0000-4000-8000-00000000000d"
    c.join(d)
    r = c.offer(d, 10, {
        "kiln-skills": {TREE: {"Levels": {"a": 99, "c": 1, "not-here": 3}, "Granted": 0, "LastLevel": 1, "LastGameTotal": 0, "Revision": 1},
                        "talents:other-mod:tree": {"Levels": {"x": 1}}, "big": {"blob": "x" * 70000}, "gameShadow": {"Levels": "not a map"}},
        "other-mod": {"data": {"n": 1}},
        "bad/mod": {"x": 1},
    })
    s.check("an offer from a client is taken", r is not None and r["Accepted"], r)
    s.check("a mod not loaded here is kept, not applied", r is not None and "other-mod" in r["Kept"] and "kiln-skills" in r["Applied"], r)
    s.check("a too big value and a bad mod id are dropped", r is not None and "kiln-skills/big" in r["Dropped"] and "bad/mod" in r["Dropped"], r)
    st = c.ctl("state", character=d)
    data = json.loads(st["Data"]) if st else {}
    s.check("levels are cut to the tree's rules: a 3 of 3; c goes, its requirement doesn't hold; an unknown node stays", data.get("Levels") == {"a": 3, "not-here": 3}, data)
    s.check("points count what the tree knows", data.get("Points") == 2, data)
    c.ctl("save")
    up = c.get(CH, UPDATE, 8.0)
    s.check("the corrections go back to the client's copy", up is not None and up["Changes"].get("kiln-skills", {}).get(TREE, {}).get("Levels") == {"a": 3, "not-here": 3}, up)
    s.check("a value that doesn't read as its type is taken off the client's copy too", up is not None and "gameShadow" in up["Changes"].get("kiln-skills", {}) and up["Changes"]["kiln-skills"]["gameShadow"] is None, up)
    if up:
        c.js(CH, ACK, {"Character": d, "Revision": up["Revision"]})
    c.leave(d)
    c.drain()
    c.join(d)  # a client without its file gets everything the server holds
    c.offer(d, 0, {})
    up = c.get(CH, UPDATE, 8.0)
    ch = (up or {}).get("Changes", {})
    s.check("what this server doesn't have stays with the character: another mod's tree", "talents:other-mod:tree" in ch.get("kiln-skills", {}), up)
    s.check("what this server doesn't have stays with the character: another mod", ch.get("other-mod", {}).get("data") == {"n": 1}, up)
    c.leave(d)
    c.drain()

    # over budget: a free reset, as the game does at login
    e = "eeeeeeee-0000-4000-8000-00000000000e"
    c.join(e)
    r = c.offer(e, 5, {"kiln-skills": {TREE: {"Levels": {"a": 3, "b": 1, "c": 1}, "Granted": -4, "LastLevel": 1, "LastGameTotal": 0, "Revision": 1}}})
    st = c.ctl("state", character=e)
    data = json.loads(st["Data"]) if st else {}
    s.check("a tree that costs more than the character has is reset for free", data.get("Levels") == {}, data)
    c.leave(e)
    c.drain()

    # caps refuse the whole offer
    f = "ffffffff-0000-4000-8000-00000000000f"
    c.join(f)
    r = c.offer(f, 5, {"m": {str(i): {"x": "y" * 40000} for i in range(6)}})
    s.check("an offer over the size limit is refused", r is not None and not r["Accepted"] and "limit" in r["Reason"], r)
    r2 = c.ctl("carried", character=f)
    s.check("nothing of it was kept", r2 and json.loads(r2["Data"])["Keys"] == [], r2)
    c.data(CH, OFFER, b"{ not json")
    st = c.ctl("status")
    s.check("the channel survives a broken message", st is not None and st["Ok"], st)
    c.leave(f)

    # the server setting
    cfg = os.path.join(a.lab, "kiln", "config", "characters.json")
    json.dump({"importProgress": False}, open(cfg, "w"))
    c.drain()
    g = "99999999-0000-4000-8000-000000000009"
    h = c.join(g)
    s.check("importProgress false: hello says so", h is not None and not h["Imports"], h)
    r = c.offer(g, 7, {"kiln-skills": {TREE: {"Levels": {"a": 1}, "Granted": 0, "LastLevel": 1, "LastGameTotal": 0, "Revision": 1}}})
    s.check("importProgress false: an offer is refused", r is not None and not r["Accepted"] and "own copy" in r["Reason"], r)
    st = c.ctl("state", character=g)
    s.check("importProgress false: nothing was taken", st and json.loads(st["Data"])["Levels"] == {}, st)
    c.ctl("unlock", character=g, name="a")
    c.ctl("save")
    s.check("importProgress false: nothing is sent back; the server keeps its own copy", c.get(CH, UPDATE, 1.5) is None)
    st = c.ctl("state", character=g)
    s.check("importProgress false: the server's copy holds", st and json.loads(st["Data"])["Levels"] == {"a": 1}, st)
    c.leave(g)
    os.remove(cfg)
    save(a.sidecar, side)


def restart(c, s, a):
    side = load(a.sidecar)
    st = c.ctl("state")
    s.check("after a restart the server has the talents", st and json.loads(st["Data"])["Levels"] == {"a": 1}, st)
    st = c.ctl("quest-status")
    s.check("after a restart the quest is where it was", st and st["Data"] == "Active/two", st)
    rev = c.ctl("carried")
    server_rev = json.loads(rev["Data"])["Revision"] if rev else -1
    s.check("the revision was saved", server_rev == side["revision"], (server_rev, side["revision"]))

    # same revision: nothing to do
    h = c.join()
    s.check("hello names the server's revision", h is not None and h["Revision"] == server_rev, h)
    r = c.offer(A, side["revision"], side["mods"])
    s.check("the same revision: the server's copy stays", r is not None and not r["Accepted"], r)
    s.check("and nothing goes out", c.get(CH, UPDATE, 1.0) is None)
    c.leave()

    # a client that lost its file gets it back
    h = c.join()
    r = c.offer(A, 0, {})
    s.check("a client without its file: the server's copy is newer", r is not None and not r["Accepted"], r)
    fresh = {"revision": 0, "mods": {}}
    up = wait_update(c, fresh, a.sidecar + ".lost")
    s.check("and it gets everything back", up is not None and fresh["mods"].get("kiln-skills", {}).get(TREE, {}).get("Levels") == {"a": 1}, up)
    os.remove(a.sidecar + ".lost")
    if up:
        side = fresh
        save(a.sidecar, side)
    c.leave()


def other(c, s, a):
    side = load(a.sidecar)
    world = c.ctl("world-id")
    print("INFO this server's world store is new; world", world["Data"] if world else "?", flush=True)
    st = c.ctl("state")
    s.check("the other server doesn't know the character", st and json.loads(st["Data"])["Levels"] == {}, st)
    h = c.join()
    s.check("hello: it has nothing yet", h is not None and h["Revision"] <= side["revision"], h)
    r = c.offer(A, side["revision"], side["mods"])
    s.check("the character's file is taken", r is not None and r["Accepted"], r)
    st = c.ctl("state")
    s.check("the same talents on the other server", st and json.loads(st["Data"])["Levels"] == {"a": 1}, st)
    n = c.ctl("note-get")
    s.check("the same carried data", n and n["Data"] == "in my bag", n)
    q = c.ctl("quest-status")
    s.check("separate quests: nothing of the first world's quest here", q and q["Data"].startswith("Available"), q)
    c.ctl("quest-start")
    q = c.ctl("quest-status")
    s.check("the quest starts over on this world", q and q["Data"] == "Active/one", q)
    u = c.ctl("unlock", name="a")
    s.check("a talent level here", u and u["Ok"], u)
    c.ctl("save")
    up = wait_update(c, side, a.sidecar)
    s.check("it goes to the client's copy", up is not None and side["mods"]["kiln-skills"][TREE]["Levels"] == {"a": 2}, up)
    s.check("the revision goes on from the character's", up is not None and up["Revision"] == side["revision"], up)
    c.leave()
    save(a.sidecar, side)


def back(c, s, a):
    side = load(a.sidecar)
    st = c.ctl("state")
    s.check("the first server still has the old talents before the character shows up", st and json.loads(st["Data"])["Levels"] == {"a": 1}, st)
    c.join()
    r = c.offer(A, side["revision"], side["mods"])
    s.check("the character is newer than this server's copy", r is not None and r["Accepted"], r)
    st = c.ctl("state")
    s.check("talents follow the character: level 2 from the other world", st and json.loads(st["Data"])["Levels"] == {"a": 2}, st)
    q = c.ctl("quest-status")
    s.check("this world's quest is where this world left it", q and q["Data"] == "Active/two", q)
    c.leave()


H = "12121212-0000-4000-8000-000000000012"


def hosted_file(a):
    return os.path.join(a.lab, "kiln", "data", "characters", H.replace("-", "") + ".json")


def hosted1(c, s, a):
    f = hosted_file(a)
    if os.path.exists(f):
        os.remove(f)
    c.js(CH, LABJOIN, {"Character": H, "Account": ACCOUNT, "Local": True})
    c.pump(0.5)
    s.check("no hello for this machine's player", c.get(CH, HELLO, 0.3) is None)
    u = c.ctl("unlock", character=H, name="a")
    s.check("a talent", u and u["Ok"], u)
    c.ctl("note-set", character=H, text="hosted")
    c.ctl("save")
    c.pump(0.5)
    s.check("the save writes the sidecar", os.path.exists(f), f)
    side = json.load(open(f))
    s.check("with the character, a revision and the data", side["character"] == H and side["revision"] == 1 and side["mods"]["kiln-skills"][TREE]["Levels"] == {"a": 1}, side)
    s.check("no update over the network for this machine's player", c.get(CH, UPDATE, 0.5) is None)
    c.ctl("quest-start", character=H)
    c.ctl("save")
    c.pump(0.5)
    side2 = json.load(open(f))
    s.check("quests don't go to the file", "kiln-quests" not in side2["mods"] and side2["revision"] == 1, list(side2["mods"]))
    u = c.ctl("unlock", character=H, name="a")
    c.ctl("save")
    c.pump(0.5)
    side3 = json.load(open(f))
    s.check("the next change: a new revision", side3["revision"] == 2 and side3["mods"]["kiln-skills"][TREE]["Levels"] == {"a": 2}, side3["revision"])
    s.check("the older file stays as .prev", json.load(open(f + ".prev"))["revision"] == 1)
    c.leave(H)


def hosted2(c, s, a):
    f = hosted_file(a)
    st = c.ctl("state", character=H)
    s.check("a wiped world store knows nothing", st and json.loads(st["Data"])["Levels"] == {}, st)
    c.js(CH, LABJOIN, {"Character": H, "Account": ACCOUNT, "Local": True})
    c.pump(0.5)
    st = c.ctl("state", character=H)
    s.check("the sidecar brings the talents back", st and json.loads(st["Data"])["Levels"] == {"a": 2}, st)
    n = c.ctl("note-get", character=H)
    s.check("and the carried data", n and n["Data"] == "hosted", n)
    q = c.ctl("quest-status", character=H)
    s.check("not the quests: they stayed with the wiped world", q and q["Data"].startswith("Available"), q)
    c.leave(H)
    # a broken main file: the older copy serves. The store still holds revision 2, so this only reads the fallback.
    open(f, "w").write("{ not json")
    c.js(CH, LABJOIN, {"Character": H, "Account": ACCOUNT, "Local": True})
    c.pump(0.5)
    c.ctl("unlock", character=H, name="a")
    c.ctl("save")
    c.pump(0.5)
    side = json.load(open(f))
    s.check("a broken file is rewritten from its older copy and the server's state", side["revision"] >= 3 and side["mods"]["kiln-skills"][TREE]["Levels"] == {"a": 3}, side)
    s.check("the broken file did not replace the good older copy", json.load(open(f + ".prev"))["revision"] >= 1)
    c.leave(H)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--lab", required=True)
    ap.add_argument("--sidecar", required=True)
    ap.add_argument("--phase", required=True, choices=["first", "restart", "other", "back", "hosted1", "hosted2"])
    ap.add_argument("--verbose", action="store_true")
    a = ap.parse_args()
    s = Suite()
    c = start(a.port, a.verbose)
    {"first": first, "restart": restart, "other": other, "back": back, "hosted1": hosted1, "hosted2": hosted2}[a.phase](c, s, a)
    print(f"charnet {a.phase}: {s.passed} PASS, {s.failed} FAIL", flush=True)
    return 1 if s.failed else 0


if __name__ == "__main__":
    sys.exit(main())
