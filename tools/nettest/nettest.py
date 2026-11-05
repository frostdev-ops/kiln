#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Fake Kiln clients for kiln.net's lab transport (docs/kiln/network.md, "Lab tests").

A lab server with "labPort" in kiln/config/network.json (or KILN_NET_LAB_PORT) accepts Kiln frames as UDP datagrams on
127.0.0.1:<port>; each source address is one peer. This script speaks Kiln's protocol (HELLO/WELCOME/REJECT, DATA,
FRAGMENT, PING/PONG, CHANNELS) like a Kiln client would over the game connection, and drives the net-test mod
(mods/tests/net-test) on the server.

    tools/nettest/nettest.py [--port 25881] [--lab ~/srv/enshrouded/labs/<lab>] [--only NAME[,NAME...]] [--verbose]

Prints PASS/FAIL lines and exits non-zero on any FAIL.
"""
import argparse
import json
import os
import random
import re
import socket
import struct
import sys
import time
import zlib

MAGIC = b"KN"
VERSION = 1
PROTOCOL = 1
T_HELLO, T_WELCOME, T_REJECT, T_DATA, T_FRAG, T_PING, T_PONG, T_BYE, T_CHANNELS = range(1, 10)
KNOCK = MAGIC + bytes([VERSION, 10])  # kiln.net's proof frame (kiln_net.h)
LAB_VANILLA = b"KLABv"  # lab transport only: a peer that sends nothing on Kiln's channel
LAB_READY = b"KLABr"    # ... and its Gameplay Ready
MAX_FRAME = 8000
DATA_HDR = 4 + 4
FRAG_HDR = 4 + 2 + 4 + 2 + 2

# net-test:echo v1 message ids (registration order in NetTestMod.cs)
M_ECHO, M_BLOB, M_BLOBACK, M_SHOUT, M_COUNTER, M_CONTROL = range(6)


# ---------------------------------------------------------------------------------------------------------- codec
class W:
    def __init__(self):
        self.b = bytearray()

    def u8(self, v): self.b += struct.pack("<B", v); return self
    def u16(self, v): self.b += struct.pack("<H", v); return self
    def u32(self, v): self.b += struct.pack("<I", v); return self
    def i32(self, v): self.b += struct.pack("<i", v); return self
    def u64(self, v): self.b += struct.pack("<Q", v); return self
    def boolean(self, v): return self.u8(1 if v else 0)

    def varu(self, v):
        while v >= 0x80:
            self.b.append((v & 0x7F) | 0x80)
            v >>= 7
        self.b.append(v)
        return self

    def string(self, s):
        e = s.encode()
        self.varu(len(e))
        self.b += e
        return self

    def blob(self, data):
        self.varu(len(data))
        self.b += data
        return self

    def raw(self, data): self.b += data; return self


class R:
    def __init__(self, b, pos=0):
        self.b, self.p = b, pos

    def take(self, n):
        if self.p + n > len(self.b):
            raise ValueError("truncated")
        s = self.b[self.p:self.p + n]
        self.p += n
        return s

    def u8(self): return self.take(1)[0]
    def u16(self): return struct.unpack("<H", self.take(2))[0]
    def u32(self): return struct.unpack("<I", self.take(4))[0]
    def i32(self): return struct.unpack("<i", self.take(4))[0]
    def u64(self): return struct.unpack("<Q", self.take(8))[0]
    def boolean(self): return self.u8() != 0

    def varu(self):
        v = shift = 0
        while True:
            c = self.u8()
            v |= (c & 0x7F) << shift
            if not c & 0x80:
                return v
            shift += 7

    def string(self): return self.take(self.varu()).decode()
    def blob(self): return bytes(self.take(self.varu()))
    def rest(self): return bytes(self.take(len(self.b) - self.p))


def frame(t):
    return W().raw(MAGIC).u8(VERSION).u8(t)


# ---------------------------------------------------------------------------------------------------------- client
class Client:
    def __init__(self, port, name, verbose=False):
        self.addr = ("127.0.0.1", port)
        self.s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.s.bind(("127.0.0.1", 0))
        self.s.settimeout(0.05)
        self.name, self.verbose = name, verbose
        self.channels = {}   # name -> id (server table, versions matching ours)
        self.table = []
        self.welcome = None
        self.reject = None
        self.reject_props = None   # properties after REJECT's details (Kiln 0.2+); the 0.1 decoder stops before them
        self.frame_len = {}        # last frame size per type
        self.messages = []   # (channel_id, msg_id, body)
        self.partial = {}
        self.seq = 0
        self.pings = 0

    def send(self, data):
        self.s.sendto(bytes(data), self.addr)

    def hello(self, mods, channels, props=None, protocol=PROTOCOL):
        w = frame(T_HELLO).u16(protocol).string("0.1.0-nettest").u32(random.getrandbits(32))
        w.varu(len(mods))
        for mid, ver, side in mods:
            w.string(mid).string(ver).u8(side)
        w.varu(len(channels))
        for cname, cver, req in channels:
            w.u16(0).string(cname).string(cver).boolean(req)
        props = props or {"nettest.client": self.name}
        w.varu(len(props))
        for k, v in props.items():
            w.string(k).string(v)
        self.send(w.b)

    def data(self, chan, msg, body):
        cid = self.channels[chan] if isinstance(chan, str) else chan
        if DATA_HDR + len(body) <= MAX_FRAME:
            self.send(frame(T_DATA).u16(cid).u16(msg).raw(body).b)
            return 1
        self.seq += 1
        chunk0 = MAX_FRAME - FRAG_HDR - 6
        chunk = MAX_FRAME - FRAG_HDR
        count = 1 + (len(body) - chunk0 + chunk - 1) // chunk
        off = 0
        for i in range(count):
            n = min(chunk0 if i == 0 else chunk, len(body) - off)
            w = frame(T_FRAG).u16(cid).u32(self.seq).u16(i).u16(count)
            if i == 0:
                w.u16(msg).u32(len(body))
            w.raw(body[off:off + n])
            off += n
            self.send(w.b)
        return count

    def pump(self, seconds):
        end = time.time() + seconds
        while time.time() < end:
            try:
                b, _ = self.s.recvfrom(70000)
            except socket.timeout:
                continue
            self.on_frame(b)

    def wait(self, pred, seconds=5.0):
        end = time.time() + seconds
        while time.time() < end:
            if pred():
                return True
            self.pump(0.05)
        return pred()

    def on_frame(self, b):
        if len(b) < 4 or b[:2] != MAGIC or b[2] != VERSION:
            return
        t = b[3]
        self.frame_len[t] = len(b)
        r = R(b, 4)
        if t == T_WELCOME:
            proto, kv, nonce, policy, maxmsg = r.u16(), r.string(), r.u32(), r.string(), r.u32()
            mods = [(r.string(), r.string(), r.u8()) for _ in range(r.varu())]
            table = [(r.u16(), r.string(), r.string(), r.boolean()) for _ in range(r.varu())]
            warns = [r.string() for _ in range(r.varu())]
            props = {r.string(): r.string() for _ in range(r.varu())} if r.p < len(b) else {}
            self.welcome = dict(protocol=proto, kiln=kv, policy=policy, max=maxmsg, mods=mods, table=table, warnings=warns, props=props)
            self.set_table(table)
        elif t == T_CHANNELS:
            self.set_table([(r.u16(), r.string(), r.string(), r.boolean()) for _ in range(r.varu())])
        elif t == T_REJECT:
            # the 0.1 decoder: reason + details, anything after is ignored
            self.reject = (r.string(), [r.string() for _ in range(r.varu())])
            self.reject_props = {r.string(): r.string() for _ in range(r.varu())} if r.p < len(b) else {}
        elif t == T_DATA:
            self.messages.append((r.u16(), r.u16(), r.rest()))
        elif t == T_FRAG:
            cid, seq, idx, cnt = r.u16(), r.u32(), r.u16(), r.u16()
            if idx == 0:
                msg, total = r.u16(), r.u32()
                self.partial[seq] = [cid, msg, total, bytearray(), 0, cnt]
            p = self.partial.get(seq)
            if p is None:
                return
            p[3] += r.rest()
            p[4] += 1
            if p[4] == p[5]:
                del self.partial[seq]
                if len(p[3]) == p[2]:
                    self.messages.append((p[0], p[1], bytes(p[3])))
        elif t == T_PING:
            self.pings += 1
            self.send(frame(T_PONG).raw(b[4:16]).b)

    def set_table(self, table):
        self.table = table
        ours = {"net-test:echo": "1", "net-test:extra": "2", "netsample:main": "1"}
        self.channels = {n: i for i, n, v, _ in table if ours.get(n) == v}

    def take(self, chan, msg, seconds=5.0):
        cid = self.channels.get(chan)
        for _ in range(int(seconds / 0.05) + 1):
            for k, m in enumerate(self.messages):
                if m[0] == cid and m[1] == msg:
                    del self.messages[k]
                    return m[2]
            self.pump(0.05)
        return None


def advert(props):
    """The kiln.* server properties of a REJECT/WELCOME (docs/kiln/network.md "Server modpacks")."""
    req = []
    i = 0
    while True:
        key = "kiln.required" if i == 0 else f"kiln.required.{i}"
        if key not in props:
            break
        req += json.loads(props[key])
        i += 1
    return {"id": props.get("kiln.server.id"), "name": props.get("kiln.server.name"), "kiln": props.get("kiln.server.kiln"),
            "modpack": props.get("kiln.modpack.url"), "sha256": props.get("kiln.modpack.sha256"), "required": req,
            "count": int(props.get("kiln.required.count", len(req))), "more": int(props.get("kiln.required.more", 0)),
            "chunks": i}


def lab_required(lab):
    """What the server should advertise: kiln/mods/* with clients "mod" (default from side), kdh1 by the reference."""
    sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "schema", "fixtures", "dirhash"))
    import dirhash
    out = []
    mods = os.path.join(lab, "kiln", "mods")
    for name in sorted(os.listdir(mods)):
        d = os.path.join(mods, name)
        mf = os.path.join(d, "kiln.mod.json")
        if name.startswith(".") or not os.path.isfile(mf) or os.path.exists(os.path.join(d, ".disabled")):
            continue
        m = json.load(open(mf))
        clients = m.get("clients") or ("none" if m.get("side", "server") == "server" else "mod")
        if clients == "mod":
            out.append({"id": m["id"], "version": m["version"], "hash": dirhash.kdh1(d)})
    return sorted(out, key=lambda x: x["id"])


NET_TEST_MOD = ("net-test", "0.1.0", 2)
CHANNELS = [("net-test:echo", "1", True), ("net-test:extra", "2", False)]


FULL_MODS = [NET_TEST_MOD]  # replaced by the server's own list (probe_server)


def probe_server(port):
    mods = [NET_TEST_MOD]
    for _ in range(3):
        c = Client(port, "probe")
        c.hello(mods, CHANNELS)
        c.wait(lambda: c.welcome is not None or c.reject is not None, 8)
        if c.welcome is not None:
            FULL_MODS[:] = [(m, v, s) for m, v, s in c.welcome["mods"]]
            known = {n for n, _, _ in CHANNELS}
            CHANNELS.extend((n, v, r) for _, n, v, r in c.welcome["table"] if n not in known)
            return
        if c.reject is None:
            return
        # The default policy refuses a client without a mod that states clients: "mod": ask again as one that has them.
        mods += [(d.split()[0], d.split()[1], NET_TEST_MOD[2]) for d in c.reject[1]]


def ready_client(port, name, verbose):
    c = Client(port, name, verbose)
    c.hello(FULL_MODS, CHANNELS)
    c.wait(lambda: c.welcome is not None, 8)
    return c


# ---------------------------------------------------------------------------------------------------------- tests
class Suite:
    def __init__(self, port, verbose, lab=None, game_templates=0):
        self.port, self.verbose, self.lab, self.game_templates = port, verbose, lab, game_templates
        self.passed = self.failed = 0

    def check(self, name, ok, detail=""):
        if ok:
            self.passed += 1
        else:
            self.failed += 1
        print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + detail if detail else ''}", flush=True)

    def control(self, c, cmd):
        c.data("net-test:echo", M_CONTROL, W().string(cmd).b)
        body = c.take("net-test:echo", M_ECHO, 5)
        return R(body).string() if body else None

    def t_handshake(self):
        c = Client(self.port, "hs", self.verbose)
        c.hello(FULL_MODS, CHANNELS)
        ok = c.wait(lambda: c.welcome is not None, 8)
        self.check("handshake.welcome", ok, json.dumps(c.welcome)[:300] if ok else "no WELCOME in 8 s (is the lab up with labPort set?)")
        if not ok:
            raise SystemExit(2)
        w = c.welcome
        self.check("handshake.channels", "net-test:echo" in c.channels and "net-test:extra" in c.channels, str(w["table"]))
        self.check("handshake.server-mods", any(m[0] == "net-test" for m in w["mods"]), str(w["mods"]))
        self.check("handshake.no-warnings", not w["warnings"], str(w["warnings"]))
        self.check("handshake.property", w["props"].get("net-test.hello", "").startswith("server-"), str(w["props"]))
        # a second HELLO from a ready client is answered again (channel update path)
        c.welcome = None
        c.hello(FULL_MODS, CHANNELS)
        self.check("handshake.rehello", c.wait(lambda: c.welcome is not None, 5))

    def t_echo(self):
        c = ready_client(self.port, "echo", self.verbose)
        c.data("net-test:echo", M_ECHO, W().string("hello ✓").b)
        body = c.take("net-test:echo", M_ECHO)
        got = R(body).string() if body else None
        self.check("echo.roundtrip", got == "echo:hello ✓", repr(got))
        t0 = time.time()
        n = 200
        for i in range(n):
            c.data("net-test:echo", M_ECHO, W().string(f"n{i}").b)
        got = []
        while len(got) < n and time.time() - t0 < 10:
            b = c.take("net-test:echo", M_ECHO, 1)
            if b is None:
                break
            got.append(R(b).string())
        self.check("echo.burst-200-ordered", got == [f"echo:n{i}" for i in range(n)],
                   f"{len(got)} replies in {time.time() - t0:.2f} s" + ("" if got == [f"echo:n{i}" for i in range(len(got))] else " (out of order)"))

    def t_json(self):
        c = ready_client(self.port, "json", self.verbose)
        c.data("net-test:echo", M_COUNTER, json.dumps({"Name": "x", "Count": 41}).encode())
        b = c.take("net-test:echo", M_COUNTER)
        got = json.loads(b) if b else None
        self.check("json.roundtrip", got == {"Name": "x", "Count": 42}, repr(got))

    def t_fragments(self):
        c = ready_client(self.port, "frag", self.verbose)
        for size in (7990, 8000, 20000, 100_000, 250_000):
            data = os.urandom(size)
            frames = c.data("net-test:echo", M_BLOB, W().boolean(True).blob(data).b)
            ack = c.take("net-test:echo", M_BLOBACK, 8)
            got = struct.unpack("<iI", ack) if ack else None
            self.check(f"fragments.to-server-{size}", got == (len(data), zlib.crc32(data)), f"{frames} frame(s), ack {got}")
            back = c.take("net-test:echo", M_BLOB, 8)
            if back:
                rr = R(back)
                rr.boolean()
                echoed = rr.blob()
            else:
                echoed = None
            self.check(f"fragments.to-client-{size}", echoed == data, f"{len(echoed) if echoed else None} bytes back")

    def t_oversize(self):
        c = ready_client(self.port, "big", self.verbose)
        # claims 64 MiB: the server drops it and stays usable
        c.send(frame(T_FRAG).u16(c.channels["net-test:echo"]).u32(999).u16(0).u16(9000).u16(M_BLOB).u32(64 << 20).raw(b"x" * 100).b)
        c.data("net-test:echo", M_ECHO, W().string("after").b)
        b = c.take("net-test:echo", M_ECHO)
        self.check("limits.oversize-dropped", b is not None and R(b).string() == "echo:after")
        data = os.urandom(300_000)  # over the 256 KiB default
        c.data("net-test:echo", M_BLOB, W().boolean(False).blob(data).b)
        self.check("limits.over-max-message-dropped", c.take("net-test:echo", M_BLOBACK, 3) is None)

    def t_garbage(self):
        c = ready_client(self.port, "junk", self.verbose)
        cid = c.channels["net-test:echo"]
        junk = [b"", b"K", b"KN\x01", b"KN\x02\x04", b"XXXX" * 10, frame(T_DATA).b, frame(T_DATA).u16(cid).b,
                frame(T_DATA).u16(cid).u16(M_ECHO).raw(b"\xff\xff\xff").b, frame(T_HELLO).u16(1).b,
                frame(T_DATA).u16(0xFFFF).u16(0).raw(b"\x00").b, frame(T_FRAG).u16(cid).u32(5).u16(3).u16(2).b,
                frame(77).raw(os.urandom(64)).b] + [os.urandom(random.randint(1, 1500)) for _ in range(50)]
        for j in junk:
            c.send(j)
        c.data("net-test:echo", M_ECHO, W().string("still").b)
        b = c.take("net-test:echo", M_ECHO)
        self.check("robust.garbage", b is not None and R(b).string() == "echo:still", f"{len(junk)} bad frames sent")

    def t_broadcast(self):
        a = ready_client(self.port, "bc-a", self.verbose)
        b = ready_client(self.port, "bc-b", self.verbose)
        a.data("net-test:echo", M_SHOUT, W().string("hi").b)
        ga = a.take("net-test:echo", M_SHOUT)
        gb = b.take("net-test:echo", M_SHOUT)
        self.check("broadcast.both", ga is not None and gb is not None and R(gb).string() == "all:hi",
                   f"a {R(ga).string() if ga else None}, b {R(gb).string() if gb else None}")

    def t_channel_mismatch(self):
        c = Client(self.port, "mismatch", self.verbose)
        c.hello(FULL_MODS, [("net-test:echo", "2", True)] + [x for x in CHANNELS if x[0] != "net-test:echo"])
        c.wait(lambda: c.welcome is not None, 8)
        self.check("sync.channel-version-warned", c.welcome is not None and any("net-test:echo" in w for w in c.welcome["warnings"]),
                   str(c.welcome["warnings"] if c.welcome else None))
        # the client thinks it has v2: the server's table offers v1, so nothing is shared
        c.channels = {n: i for i, n, v, _ in c.table if v == "2" and n == "net-test:echo"}
        self.check("sync.channel-not-shared", "net-test:echo" not in c.channels)

    def t_rate_limit(self):
        c = ready_client(self.port, "flood", self.verbose)
        cid = c.channels["net-test:echo"]
        body = W().string("x").b
        for _ in range(1500):  # stays under floodKickDrops (2000 drops in 10 s)
            c.send(frame(T_DATA).u16(cid).u16(M_ECHO).raw(body).b)
        got = 0
        end = time.time() + 6
        while time.time() < end:
            if c.take("net-test:echo", M_ECHO, 0.2) is None and got > 0:
                break
            got += 1
        self.check("limits.rate", 300 <= got < 1500, f"{got} of 1500 answered (limit 200/s, burst 400)")
        time.sleep(2.5)
        c.data("net-test:echo", M_ECHO, W().string("recovered").b)
        b = c.take("net-test:echo", M_ECHO)
        self.check("limits.rate-recovers", b is not None and R(b).string() == "echo:recovered")

    def t_ping(self):
        c = ready_client(self.port, "ping", self.verbose)
        c.send(frame(T_PING).u32(1234).u64(int(time.time() * 1000)).b)
        ok = False
        end = time.time() + 3
        while time.time() < end and not ok:
            try:
                b, _ = c.s.recvfrom(70000)
            except socket.timeout:
                continue
            ok = b[3] == T_PONG and struct.unpack("<I", b[4:8])[0] == 1234
            if not ok:
                c.on_frame(b)
        self.check("ping.pong", ok)
        c.pump(11)
        self.check("ping.server-pings", c.pings >= 1, f"{c.pings} server ping(s) in 11 s")

    def t_policy(self):
        ctl = ready_client(self.port, "ctl", self.verbose)
        try:
            r = self.control(ctl, "policy refuse")
            self.check("policy.set-refuse", r is not None and "Refuse" in r, repr(r))
            c = Client(self.port, "nomods", self.verbose)
            c.hello([], CHANNELS)
            c.wait(lambda: c.reject is not None or c.welcome is not None, 8)
            self.check("policy.refuse-missing-mod", c.reject is not None and c.welcome is None,
                       f"reject {c.reject}" if c.reject else f"welcome {c.welcome and c.welcome['warnings']}")
            # after the reject the server forgets the lab peer (kick): a fresh hello from the same socket starts over
            time.sleep(3.5)
            c.reject = None
            c.hello(FULL_MODS, CHANNELS)
            c.wait(lambda: c.welcome is not None or c.reject is not None, 8)
            self.check("policy.refuse-then-fixed", c.welcome is not None, f"reject {c.reject}")
            v = Client(self.port, "oldver", self.verbose)
            v.hello([("net-test", "0.0.9", 2)] + [m for m in FULL_MODS if m[0] != "net-test"], CHANNELS)
            v.wait(lambda: v.reject is not None or v.welcome is not None, 8)
            self.check("policy.refuse-version", v.reject is not None, str(v.reject))
            r = self.control(ctl, "match any")
            v2 = Client(self.port, "oldver2", self.verbose)
            v2.hello([("net-test", "0.0.9", 2)] + [m for m in FULL_MODS if m[0] != "net-test"], CHANNELS)
            v2.wait(lambda: v2.reject is not None or v2.welcome is not None, 8)
            self.check("policy.match-any-accepts", v2.welcome is not None, f"{r}; reject {v2.reject}")
            self.control(ctl, "match exact")
            r = self.control(ctl, "policy warn")
            w = Client(self.port, "warned", self.verbose)
            w.hello([("extra-mod", "1.0.0", 2)], CHANNELS)
            w.wait(lambda: w.welcome is not None or w.reject is not None, 8)
            self.check("policy.warn-accepts-with-warnings",
                       w.welcome is not None and any("net-test" in x for x in w.welcome["warnings"]) and any("extra-mod" in x for x in w.welcome["warnings"]),
                       str(w.welcome["warnings"] if w.welcome else w.reject))
            proto = Client(self.port, "proto9", self.verbose)
            self.control(ctl, "policy refuse")
            proto.hello(FULL_MODS, CHANNELS, protocol=9)
            proto.wait(lambda: proto.reject is not None or proto.welcome is not None, 8)
            self.check("policy.protocol-mismatch-refused", proto.reject is not None, str(proto.reject))
        finally:
            self.control(ctl, "policy warn")

    def t_reload(self):
        """Hot reload of net-test while a client is connected: its channels close and reopen with new ids, the client
        gets CHANNELS updates and keeps working; then Kiln's reload-test checks the mod's load context is collected."""
        if not self.lab:
            self.check("reload", True, "skipped (no --lab dir)")
            return
        c = ready_client(self.port, "reload", self.verbose)
        before = dict(c.channels)
        ctl = os.path.join(self.lab, "kiln", "control")
        res = os.path.join(ctl, "nettest-reload.result.json")
        if os.path.exists(res):
            os.remove(res)
        with open(os.path.join(ctl, "nettest-reload.cmd"), "w") as f:
            f.write("reload net-test")
        ok = c.wait(lambda: os.path.exists(res) and c.channels and c.channels != before, 15)
        self.check("reload.channels-updated", ok, f"before {before}, after {c.channels}")
        c.data("net-test:echo", M_ECHO, W().string("reloaded").b)
        b = c.take("net-test:echo", M_ECHO)
        self.check("reload.echo-after", b is not None and R(b).string() == "echo:reloaded")
        res = os.path.join(ctl, "nettest-leak.result.json")
        if os.path.exists(res):
            os.remove(res)
        with open(os.path.join(ctl, "nettest-leak.cmd"), "w") as f:
            f.write("reload-test net-test 3")
        end = time.time() + 40
        while time.time() < end and not os.path.exists(res):
            c.pump(0.5)
        r = json.load(open(res)) if os.path.exists(res) else None
        self.check("reload.no-leak", r is not None and r.get("ok") is True, json.dumps(r)[:300] if r else "no result in 40 s")

    def t_netsample(self):
        """mods/samples/NetSample on the server: Ping -> Pong, and Announce to other Kiln clients on join."""
        a = Client(self.port, "ns-a", self.verbose)
        a.hello(FULL_MODS, CHANNELS)
        a.wait(lambda: a.welcome is not None, 8)
        if "netsample:main" not in a.channels:
            self.check("netsample", True, "skipped (netsample not on the server)")
            return
        a.data("netsample:main", 0, W().u64(12345).string("ping #1").b)
        b = a.take("netsample:main", 1)
        got = None
        if b:
            r = R(b)
            got = (r.u64(), r.string(), r.string(), r.u64())
        self.check("netsample.pong", got is not None and got[0] == 12345 and got[1] == "ping #1" and got[2].startswith("lab-"), str(got))
        c = Client(self.port, "ns-b", self.verbose)
        c.hello(FULL_MODS, CHANNELS)
        c.wait(lambda: c.welcome is not None, 8)
        ann = a.take("netsample:main", 2, 5)
        self.check("netsample.announce", ann is not None and "joined with Kiln" in R(ann).string(), R(ann).string() if ann else "none")

    def t_advert(self):
        """Server modpack properties in WELCOME and REJECT; the kdh1 the server computed (C# under Wine) against the
        Python reference over the same folders; 0.1-style decoding of the new frames; modpack/advertise switches."""
        ctl = ready_client(self.port, "adv-ctl", self.verbose)
        try:
            c = ready_client(self.port, "adv", self.verbose)
            w = c.welcome
            a = advert(w["props"]) if w else {}
            sid = open(os.path.join(self.lab, "kiln", "data", "kiln-network", "server-id")).read().strip() if self.lab else a.get("id")
            self.check("advert.welcome-props", w is not None and a["id"] == sid and a["kiln"] and a["count"] == len(a["required"]) and
                       a["more"] == 0 and c.frame_len[T_WELCOME] <= 7 * 1024,
                       f"{c.frame_len.get(T_WELCOME)} bytes, id {a.get('id')}, name {a.get('name')}, required {a.get('required')}")
            if self.lab:
                want = lab_required(self.lab)
                got = sorted(({k: r[k] for k in ("id", "version", "hash")} for r in a["required"] if r.get("kind", "kiln") == "kiln"),
                             key=lambda x: x["id"])
                self.check("advert.kdh1-matches-reference", got == want, f"server {got} / reference {want}")
                # schema/fixtures/dirhash cases hashed by the server's C# port (under Wine)
                fx = os.path.join(os.path.dirname(__file__), "..", "..", "schema", "fixtures", "dirhash")
                dst = os.path.join(self.lab, "kiln", "data", "net-test", "dirhash")
                import shutil
                shutil.rmtree(dst, ignore_errors=True)
                shutil.copytree(os.path.join(fx, "cases"), dst, symlinks=True)
                for name in os.listdir(os.path.join(fx, "cases")):  # git drops empty folders; the fixture needs them
                    os.makedirs(os.path.join(dst, name), exist_ok=True)
                expected = json.load(open(os.path.join(fx, "expected.json")))["cases"]
                bad = []
                for name, h in sorted(expected.items()):
                    r = self.control(ctl, f"dirhash dirhash/{name}")
                    if r != f"control:{h}":
                        bad.append(f"{name}: {r}")
                self.check("advert.dirhash-fixtures-csharp", not bad, f"{len(expected)} cases" + ("; " + "; ".join(bad) if bad else ""))
            # REJECT with properties; the 0.1 decoder (reason + details) still reads it
            self.control(ctl, "policy refuse")
            j = Client(self.port, "adv-rej", self.verbose)
            j.hello([], CHANNELS)
            j.wait(lambda: j.reject is not None or j.welcome is not None, 8)
            ra = advert(j.reject_props or {})
            self.check("advert.reject-old-decoder", j.reject is not None and j.reject[0].startswith("This server requires mods") and
                       any("net-test" in d for d in j.reject[1]), str(j.reject)[:200])
            self.check("advert.reject-props", j.reject_props is not None and ra["id"] == a.get("id") and ra["required"] == a.get("required") and
                       j.frame_len[T_REJECT] <= 7 * 1024, f"{j.frame_len.get(T_REJECT)} bytes: {json.dumps(j.reject_props)[:300]}")
            self.control(ctl, "policy warn")
            # modpack in network.json -> advertised; advertise off -> no kiln.* properties at all
            sha = "ab" * 32
            r = self.control(ctl, f"modpack https://example.org/nettest-7.zip {sha}")
            m = ready_client(self.port, "adv-mp", self.verbose)
            ma = advert(m.welcome["props"]) if m.welcome else {}
            self.check("advert.modpack", ma.get("modpack") == "https://example.org/nettest-7.zip" and ma.get("sha256") == sha and
                       m.welcome["props"].get("kiln.modpack.version") == "7", f"{r}; {json.dumps({k: v for k, v in (m.welcome or {}).get('props', {}).items() if 'modpack' in k})}")
            self.control(ctl, "advertise off")
            o = ready_client(self.port, "adv-off", self.verbose)
            self.check("advert.off", o.welcome is not None and not any(k.startswith("kiln.") for k in o.welcome["props"]),
                       str(o.welcome["props"] if o.welcome else None))
        finally:
            self.control(ctl, "advertise on")
            self.control(ctl, "modpack off")
            self.control(ctl, "policy warn")

    # ---- clients without Kiln: the gate with simulated vanilla peers (lab transport); docs/kiln/network.md
    def log_mark(self):
        return os.path.getsize(self.kiln_log()) if os.path.exists(self.kiln_log()) else 0

    def kiln_log(self):
        return os.path.join(self.lab, "kiln", "logs", "kiln.log")

    def log_since(self, mark):
        with open(self.kiln_log(), "rb") as f:
            f.seek(mark)
            return f.read().decode("utf-8", "replace")

    def wait_log(self, mark, text, seconds):
        end = time.time() + seconds
        while time.time() < end:
            if text in self.log_since(mark):
                return True
            time.sleep(0.1)
        return text in self.log_since(mark)

    def gate_status(self, ctl):
        r = (self.control(ctl, "gate") or "").removeprefix("control:")
        return dict(kv.split("=") for kv in r.split()) if "=" in r else {}

    def vanilla_peer(self, name):
        """A fake client that sends nothing on Kiln's channel: only the lab's own datagrams."""
        c = Client(self.port, name, self.verbose)
        c.send(LAB_VANILLA)
        time.sleep(0.3)  # the server's next tick has the peer
        return c

    def peer_of(self, mark, count=1):
        ids = re.findall(r"net: lab peer (0x[0-9a-f]+) up", self.log_since(mark))
        return ids[count - 1] if len(ids) >= count else None

    def t_vanilla(self):
        if not self.lab:
            print("SKIP vanilla: needs --lab (reads kiln.log)", flush=True)
            return
        ctl = ready_client(self.port, "van-ctl", self.verbose)
        try:
            base = self.gate_status(ctl)
            self.check("vanilla.gate-available", int(base.get("caps", "0")) & 1 == 1, str(base))
            templates = int(base.get("templates", "0"))

            # ---- nothing at stake: no wait, under the default policy too
            if templates == 0:
                for policy in ("off", "warn"):
                    self.control(ctl, f"policy {policy}")
                    time.sleep(1.5)
                    g = self.gate_status(ctl)
                    self.check(f"vanilla.no-stake.{policy}.gate-off", g.get("mode") == "0", str(g))
                    m = self.log_mark()
                    v = self.vanilla_peer(f"van-free-{policy}")
                    t0 = time.time()
                    v.send(LAB_READY)
                    ok = self.wait_log(m, "passed the gate", 1.0)
                    self.check(f"vanilla.no-stake.{policy}.no-wait", ok and "held" not in self.log_since(m), f"{time.time() - t0:.2f} s")
                    time.sleep(6)
                    self.check(f"vanilla.no-stake.{policy}.not-judged-at-the-hold", "has no Kiln" not in self.log_since(m), self.log_since(m)[-200:])
            else:
                print(f"note: {templates} Kiln-only template(s) here: the no-wait case needs a lab without them", flush=True)

            # ---- refusing: hold, REJECT, kick at the deadline
            self.control(ctl, "policy refuse")
            time.sleep(1.5)
            g = self.gate_status(ctl)
            self.check("vanilla.refuse.gate-on", g.get("mode") == "1", str(g))
            m = self.log_mark()
            v = self.vanilla_peer("van-refuse")
            t0 = time.time()
            v.send(LAB_READY)
            self.check("vanilla.refuse.held", self.wait_log(m, "held, it gets no world data", 2), self.log_since(m)[-300:])
            self.check("vanilla.refuse.status-held", self.gate_status(ctl).get("held") == "1", str(self.gate_status(ctl)))
            ok = self.wait_log(m, "has no Kiln (vanilla client): refusing", 10)
            dt = time.time() - t0
            self.check("vanilla.refuse.at-deadline", ok and 3.5 <= dt <= 8, f"{dt:.1f} s (hold 5 s, decided on the 1 s tick)")
            self.check("vanilla.refuse.not-before", "passed the gate" not in self.log_since(m), "")
            v.wait(lambda: v.reject is not None, 3)
            self.check("vanilla.refuse.reject-text", v.reject is not None and "Kiln" in v.reject[0], str(v.reject))
            self.check("vanilla.refuse.kick", self.wait_log(m, "): rc 0", 4) and re.search(r"kick lab-\d+ \(0x[0-9a-f]+\): rc 0", self.log_since(m)) is not None, "")
            self.check("vanilla.refuse.hold-ended", self.gate_status(ctl).get("held") == "0", str(self.gate_status(ctl)))

            # ---- a Kiln client proves itself before Gameplay Ready: no wait, nothing held
            m = self.log_mark()
            k = self.vanilla_peer("van-kiln")
            k.send(KNOCK)
            time.sleep(0.3)
            t0 = time.time()
            k.send(LAB_READY)
            ok = self.wait_log(m, "passed the gate", 1.0)
            self.check("vanilla.proof-first.no-wait", ok and "held" not in self.log_since(m), f"{time.time() - t0:.2f} s")
            k.hello(FULL_MODS, CHANNELS)
            self.check("vanilla.proof-first.handshake", k.wait(lambda: k.welcome is not None, 5), "")
            self.check("vanilla.proof-first.knock-not-delivered", "dropped frame" not in self.log_since(m), "")

            # ---- the proof comes late, inside the hold: released, never judged
            m = self.log_mark()
            k = self.vanilla_peer("van-late")
            k.send(LAB_READY)
            self.wait_log(m, "held, it gets no world data", 2)
            time.sleep(1.5)
            k.send(KNOCK)
            self.check("vanilla.late-proof.released", self.wait_log(m, "in the gate: it has Kiln", 2), self.log_since(m)[-300:])
            time.sleep(6)
            self.check("vanilla.late-proof.never-judged", "has no Kiln" not in self.log_since(m), "")

            # ---- an older Kiln build: HELLO but no knock
            m = self.log_mark()
            o = self.vanilla_peer("van-old-hello")
            o.send(LAB_READY)
            self.wait_log(m, "held, it gets no world data", 2)
            o.hello(FULL_MODS, CHANNELS)
            self.check("vanilla.old-build-hello.released", self.wait_log(m, "in the gate: it has Kiln", 2), self.log_since(m)[-300:])
            self.check("vanilla.old-build-hello.welcome", o.wait(lambda: o.welcome is not None, 5), "")
            time.sleep(6)
            self.check("vanilla.old-build-hello.not-refused", "has no Kiln" not in self.log_since(m) and o.reject is None, str(o.reject))

            # ---- it never decides (Kiln.Network dead): kiln.net's own limit ends the hold
            self.control(ctl, "gate-freeze on")
            m = self.log_mark()
            f = self.vanilla_peer("van-frozen")
            t0 = time.time()
            f.send(LAB_READY)
            for _ in range(8):  # the fake clients keep the lab peers alive; the limit is 30 s
                time.sleep(4)
                f.send(LAB_VANILLA)
                if "time limit" in self.log_since(m):
                    break
            ok = self.wait_log(m, "refused by the gate's time limit", 5)
            self.check("vanilla.limit.ends-hold", ok and 25 <= time.time() - t0 <= 40, f"{time.time() - t0:.1f} s")
            self.check("vanilla.limit.nothing-held", self.gate_status(ctl).get("held") == "0", str(self.gate_status(ctl)))
            self.control(ctl, "gate-freeze off")

            # ---- let in: a lab where something can be withheld, without a mod that makes it refuse
            self.control(ctl, "policy off")
            time.sleep(1.5)
            g = self.gate_status(ctl)
            if int(g.get("templates", "0")) > 0 and int(g.get("caps", "0")) & 2 == 2:
                self.check("vanilla.admit.gate-on", g.get("mode") == "1", str(g))
                m = self.log_mark()
                a = self.vanilla_peer("van-admit")
                t0 = time.time()
                a.send(LAB_READY)
                ok = self.wait_log(m, "has no Kiln (vanilla client): letting it in", 10)
                self.check("vanilla.admit.at-deadline", ok and 3.5 <= time.time() - t0 <= 8, f"{time.time() - t0:.1f} s")
                self.check("vanilla.admit.released", self.wait_log(m, "passed the gate", 3), self.log_since(m)[-300:])
                self.check("vanilla.admit.no-reject", a.reject is None and "kick lab" not in self.log_since(m), str(a.reject))
            elif int(g.get("templates", "0")) > 0:
                # This build can't withhold them (net.withhold off): letting it in would crash it, whatever modSync says.
                m = self.log_mark()
                a = self.vanilla_peer("van-cant-withhold")
                a.send(LAB_READY)
                self.check("vanilla.cant-withhold.refused", self.wait_log(m, "has no Kiln (vanilla client): refusing", 10), self.log_since(m)[-300:])
            else:
                print(f"note: nothing to withhold here ({g}): the let-in case needs a lab with Kiln-only templates", flush=True)
        finally:
            self.control(ctl, "gate-freeze off")
            self.control(ctl, "policy warn")

    def t_withheld(self):
        """Every way Kiln adds a template shows up, and the check reads real entities (needs a lab with such mods)."""
        ctl = ready_client(self.port, "van-audit", self.verbose)
        for _ in range(4):
            # kiln.spawn reads the game's list again at most every 5 s: clones of data packs may not be in its catalog yet
            r = (self.control(ctl, "gate-audit") or "").removeprefix("control:")
            a = json.loads(r) if r.startswith("{") else None
            if a is None or a["reported"] == len(a["kiln"]):
                break
            time.sleep(6)
        self.check("withheld.audit", a is not None, str(r))
        if a is None:
            return
        print(f"audit: {a['total']} templates, {a['reported']} only Kiln has: {', '.join(a['kiln'])}", flush=True)
        self.check("withheld.audit.count-agrees", a["reported"] == len(a["kiln"]), f"kiln.assets {a['reported']}, in the world {len(a['kiln'])}")
        if self.game_templates:
            self.check("withheld.audit.against-the-game", a["total"] - self.game_templates == a["reported"],
                       f"{a['total']} in the world, {self.game_templates} in the game (--game-templates), {a['reported']} from kiln.assets")
        self.check("withheld.audit.no-kiln-name-missed", not a["missed"], str(a["missed"]))
        # a real entity of every template only Kiln has, whatever added it; a game template as the control
        cases = [("Enemy_Vukah_Melee", 0)] + [(n, 1) for n in a["kiln"][:40]]
        for name, want in cases:
            self.control(ctl, f"gate-spawn {name}")
            res = None
            for _ in range(40):
                res = (self.control(ctl, f"gate-entity {name}") or "").removeprefix("control:")
                if res != "pending":
                    break
                time.sleep(0.25)
            e = json.loads(res) if res and res.startswith("{") else None
            if e and e["withheld"] == -1 and want == 1:
                # The game can remove an NPC clone it doesn't want (kiln.spawn: "removed by the game"). Nothing to read, then;
                # the audit above has its template.
                print(f"note: {name}: the entity was gone before it could be read", flush=True)
                self.check(f"withheld.entity.{name}", name in a["kiln"], str(res))
                continue
            self.check(f"withheld.entity.{name}", e is not None and e["withheld"] == want, str(res))
            if e and want == 1:
                self.check(f"withheld.children.{name}", all(w == 1 for w in e["children"]), str(e))
            print(f"entity {name}: {res}", flush=True)
        self.gate_guard_checks(ctl, a["kiln"])
        self.control(ctl, "gate-cleanup")
        r = (self.control(ctl, "gate-hotreload") or "").removeprefix("control:")
        h = dict(kv.split("=") for kv in r.split()) if "=" in r else {}
        n = int(h.get("before", "-1"))
        self.check("withheld.hot-reload.added", h.get("unknown") == "0" and h.get("added") == "1" and h.get("addedCount") == str(n + 1), r)
        self.check("withheld.hot-reload.removed-stays", h.get("removed") == "1" and h.get("removedCount") == str(n + 1), r)
        self.check("withheld.no-entity", (self.control(ctl, "gate-noentity 4000000000") or "").removeprefix("control:") == "-1", "")

    def gate_guard_checks(self, ctl, kiln_templates):
        """The client guard and the kept-back resend, on the game's own functions in the server exe (same code as the client)."""
        slots = (self.control(ctl, "gate-slots") or "").removeprefix("control:").split(",")
        self.check("guard.free-slot-byte", slots[15] == "255", str(slots))  # the game sets it when a player takes the slot; 255 never matches
        mark = self.log_mark() if self.lab else 0
        r = (self.control(ctl, "gate-guard") or "").removeprefix("control:")
        g = dict(kv.split("=") for kv in r.split()) if "=" in r else {}
        # nothing dropped; the reader went past the moved call (a server world has no client message store, so it stops at the game's assert there)
        known = g.get("known", "").split(",")
        store = "the create reader faulted at rva" in self.log_since(mark) if self.lab else True
        self.check("guard.known-template-untouched", known[-1] == "0" and (known[0] == "0" or store), f"{g.get('known')}, assert in the message store: {store}")
        self.check("guard.drops-and-returns", g.get("rc") == "0,0,0,0" and g.get("results") == "0,0,0,0", r)
        self.check("guard.counts", g.get("missing") == "1" and g.get("dropped") == "4", r)
        if self.lab and g.get("guid"):
            time.sleep(0.3)
            lines = [l for l in self.log_since(mark).splitlines() if "this game has no template" in l]
            self.check("guard.one-line-per-template", len(lines) == 1 and g["guid"] in lines[0], str(lines))
        kiln = "Kiln_Elite_Vukah"
        if kiln not in kiln_templates:
            print("note: no Kiln_Elite_Vukah here: the kept-back test needs CreatureTweaks", flush=True)
            return
        r = (self.control(ctl, f"gate-queue {kiln}") or "").removeprefix("control:")
        q = dict(kv.split("=") for kv in r.split()) if "=" in r else {}
        ok = q and int(q["kept"]) == int(q["start"]) and int(q["withheld"]) >= 1 and int(q["sent"]) == int(q["start"]) + 2
        self.check("guard.kept-back-then-sent", bool(ok), r)

    def t_missing_mod(self):
        """A Kiln client without a mod that states clients: "mod" is refused unless modSync says otherwise."""
        if not self.lab:
            print("SKIP missing_mod: needs --lab (reads the mods' manifests)", flush=True)
            return

        def manifest(mod):
            try:
                with open(os.path.join(self.lab, "kiln", "mods", mod, "kiln.mod.json")) as f:
                    return json.load(f)
            except (OSError, ValueError):
                return {}

        clients = {m[0]: manifest(m[0]).get("clients") for m in FULL_MODS}
        stated = [i for i, c in clients.items() if c == "mod"]
        unstated = [i for i, c in clients.items() if c is None]
        kiln = [i for i, c in clients.items() if c == "kiln"]
        ctl = ready_client(self.port, "mm-ctl", self.verbose)

        def join(name, lacking):
            c = Client(self.port, name, self.verbose)
            c.hello([m for m in FULL_MODS if m[0] not in lacking], CHANNELS)
            c.wait(lambda: c.reject is not None or c.welcome is not None, 8)
            return c

        try:
            self.control(ctl, "policy default")
            time.sleep(0.2)
            for mod in unstated[:1]:  # a default from side doesn't count
                c = join("mm-unstated", [mod])
                self.check("missing.default.unstated-joins", c.welcome is not None and c.reject is None, f"lacks {mod}: {c.reject}")
                self.check("missing.default.unstated-warned", c.welcome is not None and any(mod in w for w in c.welcome["warnings"]), str(c.welcome and c.welcome["warnings"]))
            for mod in kiln[:1]:  # the server sends what it needs
                c = join("mm-kiln", [mod])
                self.check("missing.default.clients-kiln-joins", c.welcome is not None, f"lacks {mod}: {c.reject}")
            if not stated:
                print("note: no mod states clients: mod here: the refusal needs a lab with one", flush=True)
                return
            lacking = stated[:2]
            c = join("mm-stated", lacking)
            self.check("missing.default.refused", c.reject is not None and c.welcome is None, f"{c.reject}")
            self.check("missing.default.names-the-mods", c.reject is not None and all(m in c.reject[0] for m in lacking), str(c.reject))
            self.check("missing.default.only-those", c.reject is not None and all(any(m in d for m in lacking) for d in c.reject[1]), str(c.reject))
            a = advert(c.reject_props or {})
            self.check("missing.default.offer-data", any(r.get("id") in lacking for r in a.get("required", [])) and bool((c.reject_props or {}).get("kiln.server.id")),
                       json.dumps(a)[:300])
            # the launcher's offer: outcome "rejected" and a modpack in last-server.json, which the client takes from these properties
            sha = "ab" * 32
            self.control(ctl, f"modpack https://example.org/nettest-7.zip {sha}")
            time.sleep(0.2)
            c = join("mm-offer", lacking)
            o = advert(c.reject_props or {})
            self.check("missing.default.modpack-offer", c.reject is not None and o.get("modpack") == "https://example.org/nettest-7.zip" and o.get("sha256") == sha, json.dumps(o)[:200])
            self.control(ctl, "modpack off")
            for policy in ("warn", "off"):
                self.control(ctl, f"policy {policy}")
                time.sleep(0.2)
                c = join(f"mm-{policy}", lacking)
                self.check(f"missing.{policy}.joins", c.welcome is not None and c.reject is None, f"{c.reject}")
            self.control(ctl, "policy default")
            time.sleep(0.2)
            c = join("mm-all", [])
            self.check("missing.default.has-them-joins", c.welcome is not None and c.reject is None, f"{c.reject}")
        finally:
            self.control(ctl, "modpack off")
            self.control(ctl, "policy warn")

    def t_definitions(self):
        """A Kiln client's Kiln-only entities wait for its content ack (needs a lab with Kiln-only templates)."""
        if not self.lab:
            print("SKIP definitions: needs --lab", flush=True)
            return
        ctl = ready_client(self.port, "def-ctl", self.verbose)
        if int(self.gate_status(ctl).get("templates", "0")) == 0:
            print("note: no Kiln-only template here: nothing waits", flush=True)
            return
        with_content = [c for c in CHANNELS if c[0] != "kiln:content"] + [("kiln:content", "1", False)]
        without = [c for c in CHANNELS if c[0] != "kiln:content"]

        def join(name, channels):
            m = self.log_mark()
            c = Client(self.port, name, self.verbose)
            c.hello(FULL_MODS, channels)
            c.wait(lambda: c.welcome is not None, 8)
            peer = self.peer_of(m)
            return c, m, f"({peer})"

        # acks: released at once, whatever the result
        c, m, me = join("def-ack", with_content)
        cid = next((t[0] for t in c.welcome["table"] if t[1] == "kiln:content"), None) if c.welcome else None
        self.check("definitions.channel-shared", cid is not None, str(c.welcome and c.welcome["table"]))
        if cid is None:
            return
        time.sleep(0.5)
        mine = lambda: [l for l in self.log_since(m).splitlines() if me in l]
        self.check("definitions.ack.waits", not any("has the server's definitions" in l for l in mine()), str(mine())[-300:])
        c.data(cid, 1, W().u32(0x1234).u8(3).varu(0).varu(0).varu(0).varu(0).string("nettest").b)
        end = time.time() + 3
        while time.time() < end and not any("has the server's definitions after" in l for l in mine()):
            time.sleep(0.1)
        self.check("definitions.ack.released", any("has the server's definitions after" in l for l in mine()), str(mine())[-300:])
        self.check("definitions.ack.no-timer", not any("no content ack" in l for l in mine()), "")
        # no ack: the wait has a deadline
        c, m, me = join("def-silent", with_content)
        t0 = time.time()
        end = t0 + 10
        while time.time() < end and not any("no content ack in 5 s" in l for l in mine()):
            time.sleep(0.1)
        self.check("definitions.silent.deadline", any("no content ack in 5 s" in l for l in mine()) and 3 <= time.time() - t0 <= 8, f"{time.time() - t0:.1f} s")
        # no content channel: an ack can't come, so it doesn't wait
        c, m, me = join("def-old", without)
        time.sleep(6.5)
        self.check("definitions.no-channel.no-wait", c.welcome is not None and not any("no content ack" in l for l in mine()), str(mine())[-200:])

    def t_status(self):
        c = ready_client(self.port, "status", self.verbose)
        r = self.control(c, "status")
        self.check("server.status", r is not None and "fail=0" in r, repr(r))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=25881)
    ap.add_argument("--only", default="")
    ap.add_argument("--verbose", action="store_true")
    ap.add_argument("--lab", help="lab server dir (enables the hot-reload test through kiln/control)")
    ap.add_argument("--game-templates", type=int, default=0, help="templates of a lab without mods, for the withheld audit (kiln.spawn logs it)")
    a = ap.parse_args()
    s = Suite(a.port, a.verbose, a.lab, a.game_templates)
    probe_server(a.port)
    order = ["handshake", "echo", "json", "fragments", "oversize", "garbage", "broadcast", "channel_mismatch", "ping",
             "rate_limit", "policy", "advert", "missing_mod", "vanilla", "withheld", "definitions", "reload", "netsample", "status"]
    only = [x for x in a.only.split(",") if x]
    for name in order:
        if only and name not in only:
            continue
        t0 = time.time()
        try:
            getattr(s, "t_" + name)()
        except SystemExit:
            raise
        except Exception as e:  # a crashed test is a FAIL, the rest still runs
            s.check(name, False, f"{type(e).__name__}: {e}")
        if a.verbose:
            print(f"  ({name}: {time.time() - t0:.1f} s)", flush=True)
    print(f"nettest: {s.passed} PASS, {s.failed} FAIL", flush=True)
    sys.exit(1 if s.failed else 0)


if __name__ == "__main__":
    main()
