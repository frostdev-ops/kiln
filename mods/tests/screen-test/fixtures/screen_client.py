#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A fake Kiln client for kiln:screen on the lab transport (tools/nettest's frames): connects, asks the lab's screen-test for its fixed
messages through the console folder, and compares them byte by byte with what ScreenEffects' relay must send.

    mods/tests/screen-test/fixtures/screen_client.py --port <labPort> --lab <lab dir>
"""
import argparse
import os
import struct
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "..", "tools", "nettest"))
import nettest  # noqa: E402

CHANNEL, VERSION = "kiln:screen", "1"
MSG_PLAY, MSG_STOP, MSG_STRENGTH = 1, 2, 3
fails = 0


def check(ok, what):
    global fails
    print(("PASS " if ok else "FAIL ") + what)
    fails += not ok


class Client(nettest.Client):
    def set_table(self, table):
        self.table = table
        self.channels = {n: i for i, n, v, _ in table if n == CHANNEL and v == VERSION}


def console(lab, cmd):
    d = os.path.join(lab, "kiln", "control", "console")
    os.makedirs(d, exist_ok=True)
    tmp = os.path.join(d, f".screen-{os.getpid()}.tmp")
    with open(tmp, "w") as f:
        f.write(cmd + "\n")
    os.rename(tmp, os.path.join(d, f"screen-{int(time.time() * 1000)}.cmd"))


def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


# What kiln_screen.h's KilnScreenEffect holds after its size, in order: order (an int's bits), strength, fade_in, seconds, fade_out, exposure,
# contrast, saturation, gamma, temperature, tint[4], vignette[4], vignette_radius, vignette_softness, fill[4].
FIRST = [struct.unpack("<f", struct.pack("<i", -3))[0], 0.75, 0.25, 2, 0.5, 1.5, 1.25, 0.5, 1.1, -0.4,
         1, 128 / 255, 0, 0.5, 0x10 / 255, 0x20 / 255, 0x30 / 255, 0.6, 0.25, 0.75, 1, 1, 1, 0.2]
DEFAULTS = [0, 1, 0, -1, 0, 0, 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0.5, 0.5, 0, 0, 0, 0]


def play(body):
    token = struct.unpack_from("<I", body, 0)[0]
    fields = list(struct.unpack_from("<24f", body, 4))
    return token, fields, len(body) - 4 - 96


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--lab", required=True)
    a = ap.parse_args()
    lab = os.path.expanduser(a.lab)
    nettest.probe_server(a.port)
    c = Client(a.port, "screen-relay")
    c.hello(nettest.FULL_MODS, [(CHANNEL, VERSION, False)])
    c.wait(lambda: c.welcome is not None or c.reject is not None, 10)
    check(c.welcome is not None and CHANNEL in c.channels, f"handshake: the server has {CHANNEL} v{VERSION} (reject: {c.reject})")
    if not c.welcome:
        return 1
    entry = next(t for t in c.welcome["table"] if t[1] == CHANNEL)
    check(entry[3] is False, "the channel is not required: it never blocks a join")
    c.send(nettest.LAB_READY)
    c.pump(1.0)

    console(lab, "screentest relay")
    p1 = c.take(CHANNEL, MSG_PLAY, 15)
    p2 = c.take(CHANNEL, MSG_PLAY, 3)
    st = c.take(CHANNEL, MSG_STRENGTH, 3)
    s1 = c.take(CHANNEL, MSG_STOP, 3)
    s2 = c.take(CHANNEL, MSG_STOP, 3)
    check(p1 is not None and p2 is not None, "two plays arrived")
    if p1 is None or p2 is None:
        return 1
    t1, f1, left1 = play(p1)
    print(f"INFO play {len(p1)} B: token 0x{t1:08x} {[round(x, 4) for x in f1]}")
    check(len(p1) == 100 and left1 == 0, "a play is a token and 24 values: 100 bytes")
    check(t1 & 0x80000000 != 0, "a relayed token")
    check(struct.unpack("<i", struct.pack("<f", f1[0]))[0] == -3, "order -3, as an int's bits")
    check(all(f32(x) == y for x, y in zip(FIRST[1:], f1[1:])), "every other field, exactly: grade, fades, sRGB colours, vignette shape")
    t2, f2, _ = play(p2)
    check(f2 == [f32(x) for x in DEFAULTS] and t2 != t1, "an empty effect sends kiln_screen_effect_defaults: until stopped, changes nothing")
    check(st is not None and struct.unpack("<If", st) == (t1, f32(0.25)), "a strength: the first's token, 0.25")
    check(s1 is not None and s2 is not None, "two stops arrived")
    if s1 and s2:
        r1, r2 = nettest.R(s1), nettest.R(s2)
        a1, i1, a2, i2 = r1.u32(), r1.boolean(), r2.u32(), r2.boolean()
        check(a1 == t1 and i1 is True, "Stop(immediate): the first's token")
        check(a2 == t2 and i2 is False, "Stop: the second's token, with its fade out")
    print(f"{fails} FAIL")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
