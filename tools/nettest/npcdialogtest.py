#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Kiln.Npcs' dialogue parts in a lab (dev.32; docs/kiln/services/npc-speech.md): a dev.32 Kiln peer with kiln:npcspeech v1
and kiln:npcdialog v1, a 31.21-like Kiln peer with kiln:npcspeech v1 only, a vanilla peer, and mods/tests/npcspeech-test
driven through the console control folder. Optionally a LABBOT bot (--labbot <dir of labbot.py>): a real player entity
next to the world's NPCs, for the host's refusal to reply to a player without Kiln and for a real NPC's entity.

    tools/nettest/npcdialogtest.py --port <labPort> --lab ~/srv/enshrouded/labs/<lab> [--labbot <tree>/tools/labbot]

Checks what the host sends on each channel (Lines with their pick, Topics, Reset; v1 Speech for the old peer only), the
snapshot a dev.32 peer gets when it becomes ready, that an old peer gets nothing of kiln:npcdialog, that Opened and Picked
from a peer without a player run nothing, and that the vanilla peer gets no Kiln frame. PASS/FAIL lines; exit 1 on a FAIL.
"""
import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import nettest  # noqa: E402
from npcspeechtest import console  # noqa: E402

V1 = ("kiln:npcspeech", "1")
V2 = ("kiln:npcdialog", "1")
FAILS = []


def check(name, ok, detail=""):
    print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + detail if detail else ''}", flush=True)
    if not ok:
        FAILS.append(name)


class Peer(nettest.Client):
    def set_table(self, table):
        self.table = table
        self.ids = {i: n for i, n, v, _ in table if (n, v) in (V1, V2)}
        self.channels = {n: i for i, n in self.ids.items()}

    def decoded(self):
        out = []
        for cid, msg, body in list(self.messages):
            chan = self.ids.get(cid)
            r = nettest.R(body)
            if chan == V1[0]:
                if msg == 1:
                    npc, mode, n = r.u32(), r.u8(), r.u8()
                    out.append(("v1speech", npc, mode, [json.loads(r.string()) for _ in range(n)]))
                elif msg == 2:
                    npc, text, ms = r.u32(), r.string(), r.u32()
                    out.append(("v1say", npc, json.loads(text) if text else None, ms))
                elif msg == 3:
                    out.append(("v1reset", r.boolean()))
            elif chan == V2[0]:
                if msg == 1:
                    npc, kind, mode, pick, seed, n = r.u32(), r.u8(), r.u8(), r.u8(), r.u32(), r.u8()
                    out.append(("lines", npc, kind, mode, pick, seed, [json.loads(r.string()) for _ in range(n)]))
                elif msg == 2:
                    out.append(("next", r.u32(), r.u8(), r.i32()))
                elif msg == 3:
                    npc, n = r.u32(), r.u8()
                    rows = []
                    for _ in range(n):
                        tid, label, reply, hint, close = r.u32(), r.string(), r.string(), r.u8(), r.boolean()
                        rows.append((tid, json.loads(label), json.loads(reply) if reply else None, hint, close))
                    out.append(("topics", npc, rows))
                elif msg == 4:
                    out.append(("menusay", r.u32(), json.loads(r.string())))
                elif msg == 5:
                    out.append(("v2reset", r.boolean()))
            else:
                out.append(("other", cid, msg))
        return out

    def drain(self, seconds=1.5):
        self.pump(seconds)
        got = self.decoded()
        self.messages.clear()
        return got


def join(port, name, channels):
    p = Peer(port, name)
    p.hello([], [(c[0], c[1], False) for c in channels])
    p.wait(lambda: p.welcome is not None, 20)
    return p


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--lab", required=True)
    ap.add_argument("--labbot", help="the tools/labbot directory of a tree with LABBOT (dev32/labbot)")
    a = ap.parse_args()
    npc = 900002
    run = lambda line: console(a.lab, f"npcspeech-test {line}")  # noqa: E731

    new = join(a.port, "dialog-kiln32", [V1, V2])
    check("handshake of a dev.32 Kiln peer", new.welcome is not None, repr(new.reject))
    check("the server has kiln:npcdialog v1, not required",
          any(n == V2[0] and v == V2[1] and not req for _, n, v, req in new.table), repr(new.table))
    first = [m[0] for m in new.drain(3)]
    check("a ready dev.32 peer gets both snapshots' Resets", "v1reset" in first and "v2reset" in first, repr(first))
    old = join(a.port, "dialog-kiln3121", [V1])
    old.drain(2)
    v = nettest.Client(a.port, "dialog-vanilla")
    v.send(nettest.LAB_VANILLA)
    time.sleep(0.3)
    v.send(nettest.LAB_READY)

    status = run("status")
    print(f"INFO {status}", flush=True)
    check("the server runs the driver", status is not None and "npcspeech-test status" in status, repr(status))
    check("a dedicated server has no dialogue menu of its own (farewell row off there)",
          status is not None and "farewell row " in status and "farewell row Available" not in status, repr(status))

    r = run(f"pick {npc} inorder")
    got_new, got_old = new.drain(), old.drain()
    lines = [m for m in got_new if m[0] == "lines"]
    check("InOrder Replace accepted", r is not None and "mode Replace" in r, repr(r))
    check("the dev.32 peer gets Lines (greeting, Replace, InOrder) with the three lines",
          len(lines) == 1 and lines[0][1:6] == (npc, 0, 2, 2, 0) and [x["*"] for x in lines[0][6]] == ["No food, no work.", "My stomach...", "Is there nothing to eat?"],
          repr(got_new))
    check("the old peer gets v1 Speech only", [m[0] for m in got_old] == ["v1speech"] and got_old[0][2] == 2, repr(got_old))

    run(f"pick {npc} seeded 7")
    got_new, got_old = new.drain(), old.drain()
    check("a new pick (Seeded 7) goes to the dev.32 peer only, with its seed",
          [m[:6] for m in got_new] == [("lines", npc, 0, 2, 3, 7)] and got_old == [], f"{got_new!r} {got_old!r}")
    run(f"pick {npc} seeded 7")
    check("the same pick again sends nothing", new.drain() == [] and old.drain() == [])

    r = run(f"farewell {npc}")
    got_new, got_old = new.drain(), old.drain()
    fw = [m for m in got_new if m[0] == "lines" and m[2] == 1]
    check("Farewells: Lines (farewell, Replace, InOrder) with both languages",
          r is not None and "farewells Replace" in r and len(fw) == 1 and fw[0][3:5] == (2, 2) and
          fw[0][6][0] == {"de": "Guten Appetit!", "en": "Eat well!"}, f"{r!r} {got_new!r}")
    check("the old peer gets nothing of farewells", got_old == [], repr(got_old))

    r = run(f"menuline {npc}")
    got_new = new.drain()
    ml = [m for m in got_new if m[0] == "lines" and m[2] == 2]
    check("MenuLine with a host callback: Lines (menu line, Replace, host pick)", len(ml) == 1 and ml[0][3:5] == (2, 4), repr(got_new))
    check("a lab peer has no player: no Next for it, the callback not asked",
          not [m for m in got_new if m[0] == "next"] and r is not None and "by calls 0" in r, f"{r!r} {got_new!r}")

    r = run(f"topics {npc}")
    got_new, got_old = new.drain(), old.drain()
    tp = [m for m in got_new if m[0] == "topics"]
    check("Topics: the page goes to the dev.32 peer (two rows, hint, reply, close)",
          r is not None and "topics 2" in r and len(tp) == 1 and len(tp[0][2]) == 2 and tp[0][2][0][3] == 2 and
          tp[0][2][0][2] == {"*": "Thank you, Flameborn!"} and tp[0][2][1][4] is True, f"{r!r} {got_new!r}")
    check("the old peer gets no page", got_old == [], repr(got_old))
    feed = tp[0][2][0][0] if tp and tp[0][2] else 0

    # a dev.32 peer reports an open and a pick: it has no player here, so the host runs nothing (and keeps running)
    w = nettest.W().u32(npc).u8(2).i32(0).u32(1)
    new.data(V2[0], 6, w.b)
    new.data(V2[0], 7, nettest.W().u32(npc).u32(feed).b)
    new.pump(1)
    r = run(f"status {npc}")
    check("Opened and Picked from a peer without a player run no callback", r is not None and "by calls 0" in r and "picks 0" in r, repr(r))

    r = run(f"reply {npc} 4242")
    check("a reply to a player without Kiln is refused, saying so", r is not None and "refused" in r and "no Kiln client" in r, repr(r))

    late = join(a.port, "dialog-late", [V1, V2])
    snap = late.drain(3)
    kinds = sorted((m[0], m[2] if m[0] == "lines" else -1) for m in snap if m[0] in ("lines", "topics"))
    check("a dev.32 peer that joins later gets Reset, the three Lines and the page",
          any(m[0] == "v2reset" for m in snap) and kinds == [("lines", 0), ("lines", 1), ("lines", 2), ("topics", -1)], repr(snap))

    if a.labbot:
        sys.path.insert(0, a.labbot)
        import labbot  # noqa: E402
        lab = labbot.Lab(a.port)
        bot = lab.spawn(f"NPCD Bot {int(time.time()) % 100000}")
        who = bot.entity
        print(f"INFO bot {who}", flush=True)
        r = run(f"reply {npc} {who}")
        check("a reply to a real player (a bot) without Kiln is refused", r is not None and "no Kiln client" in r, repr(r))
        r = run(f"near 0 {who}")
        print(f"INFO {r}", flush=True)
        if r is not None and "; near " in r:
            real = int(r.split("; near ")[1].split()[0])
            run(f"farewell {real}")
            got = new.drain()
            check("a real NPC next to the bot: its farewells go out under its entity",
                  any(m[0] == "lines" and m[1] == real and m[2] == 1 for m in got), repr(got))
            run(f"farewell-clear {real}")
        else:
            print("INFO no villager with speech near the bot in this world: the real-NPC check is skipped", flush=True)
        r = run(f"status {npc}")
        check("the host's callbacks still ran for no one (the bot is no Kiln client)", r is not None and "by calls 0" in r, repr(r))
        bot.leave()

    for verb, check_msg in (("menuline-clear", ("lines", 2, 0)), ("farewell-clear", ("lines", 1, 0)), ("clear", ("lines", 0, 0))):
        run(f"{verb} {npc}")
        got = new.drain()
        check(f"{verb}: Lines of that kind back to the game's",
              any(m[0] == check_msg[0] and m[2] == check_msg[1] and m[3] == check_msg[2] for m in got), repr(got))
    run(f"topics-clear {npc}")
    got = new.drain()
    check("topics-clear: an empty page", ("topics", npc, []) in got, repr(got))
    late.drain(0.5)
    old.drain(0.5)

    v.pump(1)
    check("the vanilla peer got no Kiln frame", not v.messages and v.welcome is None, repr(v.messages[:3]))
    print(f"npcdialog lab: {'FAIL' if FAILS else 'PASS'} ({len(FAILS)} failed)", flush=True)
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
