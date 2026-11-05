#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A fake Kiln client for kiln:vfx on the lab transport (tools/nettest's frames): connects, asks the lab's vfx-test for its fixed
messages through the console folder, and compares them byte by byte with what Kiln.Vfx's relay must send.

    mods/tests/vfx-test/fixtures/relay_client.py --port <labPort> --lab <lab dir>
"""
import argparse
import math
import os
import struct
import sys
import time
import uuid

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "..", "tools", "nettest"))
import nettest  # noqa: E402

CHANNEL, VERSION = "kiln:vfx", "1"
MSG_PLAY, MSG_STOP, MSG_PARAM = 1, 2, 3
UNKNOWN = uuid.UUID("deadbeef-0bad-4bad-8bad-0000deadbeef")
FLOAT, FLOAT3, COLOR, INT = 0x90b5085d, 0x3afc452a, 0x53039620, 0x5655a2c9
fails = 0


def check(ok, what):
    global fails
    print(("PASS " if ok else "FAIL ") + what)
    fails += not ok


def fnv(s):
    h = 0x811c9dc5
    for b in s.encode():
        h = ((h ^ b) * 0x01000193) & 0xffffffff
    return h


class Client(nettest.Client):
    def set_table(self, table):
        self.table = table
        self.channels = {n: i for i, n, v, _ in table if n == CHANNEL and v == VERSION}


def console(lab, cmd):
    d = os.path.join(lab, "kiln", "control", "console")
    os.makedirs(d, exist_ok=True)
    tmp = os.path.join(d, f".relay-{os.getpid()}.tmp")
    with open(tmp, "w") as f:
        f.write(cmd + "\n")
    os.rename(tmp, os.path.join(d, f"relay-{int(time.time() * 1000)}.cmd"))


def params(r):
    out = {}
    for _ in range(r.u8()):
        h, t = r.u32(), r.u32()
        out[h] = (t, r.blob())
    return out


def play(body):
    r = nettest.R(body)
    m = {"kind": r.u8(), "guid": uuid.UUID(bytes_le=bytes(r.take(16)))}
    m["at"] = struct.unpack("<3d", r.take(24))
    m["rot"] = struct.unpack("<4f", r.take(16))
    m["scale"] = struct.unpack("<f", r.take(4))[0]
    m["entity"], m["slot"] = r.u32(), r.u8()
    m["local"] = struct.unpack("<3f", r.take(12))
    m["world"] = struct.unpack("<3f", r.take(12))
    m["orot"] = struct.unpack("<4f", r.take(16))
    m["life"], m["token"] = struct.unpack("<q", r.take(8))[0], r.u32()
    m["params"] = params(r)
    m["left"] = len(r.rest())
    return m


def close(a, b):
    return all(math.isclose(x, y, abs_tol=1e-6) for x, y in zip(a, b))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--lab", required=True)
    a = ap.parse_args()
    lab = os.path.expanduser(a.lab)
    # the server's mod list, then a client that has the channel
    nettest.probe_server(a.port)
    c = Client(a.port, "vfx-relay")
    c.hello(nettest.FULL_MODS, [(CHANNEL, VERSION, False)])
    c.wait(lambda: c.welcome is not None or c.reject is not None, 10)
    check(c.welcome is not None and CHANNEL in c.channels, f"handshake: the server has {CHANNEL} v{VERSION} (reject: {c.reject})")
    if not c.welcome:
        return 1
    entry = next(t for t in c.welcome["table"] if t[1] == CHANNEL)
    check(entry[3] is False, "the channel is not required: it never blocks a join")
    c.send(nettest.LAB_READY)
    c.pump(1.0)

    console(lab, "vfxtest relay")
    p1 = c.take(CHANNEL, MSG_PLAY, 15)
    p2 = c.take(CHANNEL, MSG_PLAY, 3)
    pm = c.take(CHANNEL, MSG_PARAM, 3)
    s1 = c.take(CHANNEL, MSG_STOP, 3)
    s2 = c.take(CHANNEL, MSG_STOP, 3)
    check(p1 is not None and p2 is not None, "two PlayMsgs arrived")
    if p1 is None or p2 is None:
        return 1
    m = play(p1)
    print(f"INFO PlayMsg {len(p1)} B: {m}")
    check(m["kind"] == 1 and m["guid"] == UNKNOWN, "PlayAt: kind and GUID")
    check(m["at"] == (1.5, 2.5, 3.5) and close(m["rot"], (0, 0, 0, 1)) and m["scale"] == 2.0, "PlayAt: position, rotation, scale")
    check(m["life"] == 3_000_000_000, "PlayAt: lifetime 3 s in ns")
    check(m["token"] & 0x80000000 != 0, "PlayAt: a remote token")
    want = {fnv("radius"): (FLOAT, struct.pack("<f", 2.5)), fnv("targetPosition"): (FLOAT3, struct.pack("<3f", 1, 2, 3)),
            fnv("color"): (COLOR, bytes([10, 20, 30, 40])), fnv("count"): (INT, struct.pack("<i", 7))}
    check(m["params"] == want, "PlayAt: four parameters: FNV-1a names, DsTypeIds, bytes")
    check(m["left"] == 0, "PlayAt: nothing left over")
    n = play(p2)
    print(f"INFO PlayMsg {len(p2)} B: {n}")
    check(n["kind"] == 2 and n["guid"] == UNKNOWN and n["entity"] != 0 and n["slot"] == 8, "PlayOn: kind, GUID, entity, slot HandR 8")
    check(close(n["local"], (0.1, 0.2, 0.3)) and close(n["world"], (1, 2, 3)) and close(n["orot"], (0, 1, 0, 0)), "PlayOn: the offset")
    check(n["life"] == -1 and n["params"] == {} and n["left"] == 0, "PlayOn: until stopped, no parameters")
    check(n["at"] != (0.0, 0.0, 0.0), f"PlayOn: the entity's position goes along: {n['at']}")

    check(pm is not None, "a ParamMsg arrived")
    if pm:
        r = nettest.R(pm)
        t, h, ty, d = r.u32(), r.u32(), r.u32(), r.blob()
        check(t == m["token"] and h == fnv("radius") and ty == FLOAT and d == struct.pack("<f", 9.0), "Set: token, name, type, value")
    check(s1 is not None and s2 is not None, "two StopMsgs arrived")
    if s1 and s2:
        r1, r2 = nettest.R(s1), nettest.R(s2)
        t1, i1, t2, i2 = r1.u32(), r1.boolean(), r2.u32(), r2.boolean()
        check(t1 == m["token"] and i1 is True, "Stop(immediate): the PlayAt's token")
        check(t2 == n["token"] and i2 is False, "Stop: the PlayOn's token")
    print(f"{fails} FAIL")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
