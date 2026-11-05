#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""KAP/1 tests against a lab server running Kiln.Admin (docs/kiln/launcher/admin-protocol.md).

    tools/nettest/kaptest.py --lab ~/srv/enshrouded/labs/<lab> [--only NAME[,NAME...]] [--verbose]

Reads <lab>/kiln/data/kiln-admin/{endpoint.json,admin-token}, then checks: the handshake (and the test vector in
schema/fixtures/kap/handshake.json), a wrong MAC / missing auth / wrong proto refused, framing errors, every op of
ready.ops answering over TCP exactly as over the file bridge (<lab>/shroudforge/exports/emberwatch), state pushes about
every second, the log topic, ping/pong, the connection and in-flight limits, admin_token_rotate. Prints PASS/FAIL lines;
exits non-zero on any FAIL. Mutating ops are only called with arguments they refuse (no side effects).

--only build (never part of the default run) edits the lab's world through the build_* ops (Kiln.Build): a box, a
prop, a schematic capture + paste turned 90 degrees, then undoes all of it and checks the cells read as before.
"""
import argparse
import hashlib
import hmac
import json
import os
import secrets
import socket
import struct
import sys
import time
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]


class Kap:
    def __init__(self, port, token=None, timeout=10.0):
        self.s = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        self.s.settimeout(timeout)
        self.token = token
        self.buf = b""
        self.pending = []  # frames read while waiting for another
        self.next_id = 1
        self.hello = self.ready = None

    def send(self, obj):
        self.send_raw(json.dumps(obj, separators=(",", ":")).encode())

    def send_raw(self, payload, length=None):
        n = len(payload) if length is None else length
        self.s.sendall(struct.pack("<I", n) + payload)

    def _read_exact(self, n):
        while len(self.buf) < n:
            chunk = self.s.recv(65536)
            if not chunk:
                raise EOFError("connection closed")
            self.buf += chunk
        out, self.buf = self.buf[:n], self.buf[n:]
        return out

    def read(self):
        if self.pending:
            return self.pending.pop(0)
        n = struct.unpack("<I", self._read_exact(4))[0]
        return json.loads(self._read_exact(n))

    def read_until(self, pred, timeout=10.0):
        end = time.time() + timeout
        keep = []
        try:
            while time.time() < end:
                self.s.settimeout(max(0.05, end - time.time()))
                try:
                    f = self.pending.pop(0) if self.pending else self.read()
                except socket.timeout:
                    break
                if pred(f):
                    return f
                keep.append(f)
            return None
        finally:
            self.pending = keep + self.pending
            self.s.settimeout(10)

    def handshake(self, mac=None, proto="kap/1"):
        self.hello = self.read()
        cnonce = secrets.token_hex(32)
        if mac is None:
            mac = hmac.new(self.token, f"kap1-auth\n{self.hello['nonce']}\n{cnonce}".encode(), hashlib.sha256).hexdigest()
        self.send({"t": "auth", "proto": proto, "cnonce": cnonce, "mac": mac, "client": "kaptest"})
        r = self.read()
        if r.get("t") == "ready":
            want = hmac.new(self.token, f"kap1-ready\n{self.hello['nonce']}\n{cnonce}".encode(), hashlib.sha256).hexdigest()
            r["_macok"] = r.get("mac") == want
            self.ready = r
        return r

    def call(self, op, args=None, timeout=60.0):
        i = self.next_id
        self.next_id += 1
        self.send({"t": "req", "id": i, "op": op, "args": args or {}})
        return self.read_until(lambda f: f.get("t") == "res" and f.get("id") == i, timeout)

    def closed(self, timeout=3.0):
        """True when the server closed the connection within timeout."""
        end = time.time() + timeout
        while time.time() < end:
            try:
                self.s.settimeout(0.2)
                if not self.s.recv(65536):
                    return True
            except socket.timeout:
                continue
            except OSError:
                return True
        return False

    def close(self):
        try:
            self.s.close()
        except OSError:
            pass


class Bridge:
    def __init__(self, folder: Path):
        self.req, self.res = folder / "requests", folder / "results"

    def call(self, op, args=None, timeout=60.0):
        rid = uuid.uuid4().hex[:16]
        body = dict(args or {})
        body["op"] = op
        tmp = self.req / f".{rid}.tmp"
        tmp.write_text(json.dumps(body))
        os.replace(tmp, self.req / f"{rid}.json")
        out = self.res / f"{rid}.json"
        end = time.time() + timeout
        while time.time() < end:
            if out.exists():
                try:
                    r = json.loads(out.read_text())
                    out.unlink()
                    return r
                except (ValueError, OSError):
                    pass
            time.sleep(0.05)
        return None


# ops called with arguments they refuse: same error over both transports, nothing changes in the world
REFUSING = {
    "teleport": {}, "teleport_to_player": {}, "give_item": {}, "spawn": {}, "spawn_item": {}, "settings_set": {},
    "broadcast": {}, "tell": {}, "command": {}, "command_run": {}, "time_set": {"hours": "x"}, "heal": {}, "god": {},
    "kill": {}, "damage": {}, "backup_create": {}, "backup_restore": {}, "zones_add": {}, "zones_remove": {},
    "content_set": {}, "content_reset": {}, "content_get": {}, "content_fields": {}, "content_search": {},
    "content_entry": {}, "content_recipe": {}, "config_get": {}, "config_set": {}, "config_reload": {},
    "permissions_check": {}, "permissions_player_node": {}, "permissions_player_group": {}, "permissions_group": {},
    "permissions_group_remove": {}, "permissions_map_user_group": {}, "mod_load": {}, "mod_reload": {}, "mod_unload": {},
    "mod_test": {}, "inventory": {}, "health": {}, "log_tail": {"lines": 0},
    "build_undo": {"owner": "kaptest-nobody"},
}
# not compared op-by-op: tested on its own (the others with side effects are harmless in a lab: a save, a reload of
# unchanged files, weather, despawning what this test never spawned)
SKIP = {"admin_token_rotate"}
# values that differ between two calls a few ms apart
VOLATILE = {"ms", "clock", "uptimeS", "connections", "time", "hours", "lastSaved", "roundTripMs", "framesIn",
            "framesOut", "dropped", "preMsPerStep", "postMsPerStep", "maxMs", "step", "steps", "tick", "now",
            "written", "bytesIn", "bytesOut", "uptime", "elapsed", "dayTime", "timeOfDay", "fired", "count", "calls",
            "revision", "generation", "savesSeen"}


def normalize(v, path=""):
    if isinstance(v, dict):
        return {k: normalize(x, path + "." + k) for k, x in sorted(v.items()) if k not in VOLATILE}
    if isinstance(v, list):
        return [normalize(x, path) for x in v]
    if isinstance(v, float):
        return round(v, 2)
    if isinstance(v, str) and v[:1] == "{":
        try:
            return normalize(json.loads(v), path)  # e.g. content_reload's report text
        except ValueError:
            pass
    return v


def first_diff(a, b, path="$"):
    if type(a) is not type(b):
        return f"{path}: {json.dumps(a)[:120]} vs {json.dumps(b)[:120]}"
    if isinstance(a, dict):
        for k in sorted(set(a) | set(b)):
            if k not in a or k not in b:
                return f"{path}.{k}: only in {'file' if k in a else 'tcp'}"
            d = first_diff(a[k], b[k], f"{path}.{k}")
            if d:
                return d
        return None
    if isinstance(a, list):
        if len(a) != len(b):
            return f"{path}: {len(a)} vs {len(b)} items"
        for i, (x, y) in enumerate(zip(a, b)):
            d = first_diff(x, y, f"{path}[{i}]")
            if d:
                return d
        return None
    return None if a == b else f"{path}: {json.dumps(a)[:120]} vs {json.dumps(b)[:120]}"


class Suite:
    def __init__(self, lab: Path, verbose):
        self.lab, self.verbose = lab, verbose
        data = lab / "kiln/data/kiln-admin"
        self.endpoint = json.loads((data / "endpoint.json").read_text())
        self.token_file = data / "admin-token"
        self.port = self.endpoint["port"]
        self.bridge = Bridge(lab / "shroudforge/exports/emberwatch")
        self.passed = self.failed = 0

    @property
    def token(self):
        return bytes.fromhex(self.token_file.read_text().strip())

    def check(self, name, ok, detail=""):
        if ok:
            self.passed += 1
        else:
            self.failed += 1
        print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + detail if detail else ''}", flush=True)

    def kap(self):
        k = Kap(self.port, self.token)
        r = k.handshake()
        if r.get("t") != "ready":
            raise RuntimeError(f"handshake failed: {r}")
        return k

    # ------------------------------------------------------------------ tests
    def t_vector(self):
        v = json.loads((REPO / "schema/fixtures/kap/handshake.json").read_text())
        tok = bytes.fromhex(v["token"])
        mac = hmac.new(tok, v["authMessage"].encode(), hashlib.sha256).hexdigest()
        smac = hmac.new(tok, v["readyMessage"].encode(), hashlib.sha256).hexdigest()
        self.check("vector.macs", mac == v["mac"] and smac == v["serverMac"])
        payload = v["helloPayload"].encode()
        self.check("vector.hello-frame", (struct.pack("<I", len(payload)) + payload).hex() == v["helloFrameHex"])

    def t_endpoint(self):
        e = self.endpoint
        tok = self.token_file.read_text()
        self.check("endpoint.json", e["format"] == 1 and e["protocol"] == "kap/1" and e["host"] == "127.0.0.1" and
                   e["token"] == "admin-token" and e.get("server", {}).get("id") and e.get("bridge"), json.dumps(e)[:300])
        self.check("endpoint.token-file", len(tok.strip()) == 64 and all(c in "0123456789abcdef" for c in tok.strip()) and
                   tok.endswith("\n"), f"{len(tok)} chars, mode {oct(self.token_file.stat().st_mode & 0o777)}")

    def t_handshake(self):
        k = Kap(self.port, self.token)
        r = k.handshake()
        h = k.hello
        self.check("handshake.hello", h["t"] == "hello" and h["proto"] == "kap/1" and len(h["nonce"]) == 64 and
                   h["auth"] == ["hmac-sha256"] and h["server"]["id"] == self.endpoint["server"]["id"], json.dumps(h)[:300])
        self.check("handshake.ready", r.get("t") == "ready" and r.get("_macok") and "status" in r["ops"] and
                   r["topics"] == ["state", "log"] and r["limits"]["maxFrame"] == 16 << 20 and r["limits"]["maxClientFrame"] == 1 << 20
                   and r["limits"]["inflight"] == 64, json.dumps({x: r[x] for x in r if x != "ops"})[:300] + f", {len(r['ops'])} ops")
        res = k.call("ping")
        self.check("handshake.req-ping", res and res["ok"] and res["op"] == "ping" and res["data"]["pong"] is True, json.dumps(res))
        k.close()

    def t_refused(self):
        k = Kap(self.port, self.token)
        t0 = time.time()
        r = k.handshake(mac="00" * 32)
        dt = time.time() - t0
        self.check("refuse.wrong-mac", r.get("t") == "error" and r.get("code") == "auth_failed" and k.closed() and dt >= 0.24,
                   f"{r} after {dt * 1000:.0f} ms")
        k.close()
        k = Kap(self.port, self.token)
        k.read()
        k.send({"t": "req", "id": 1, "op": "ping"})
        r = k.read()
        self.check("refuse.auth-required", r.get("code") == "auth_required" and k.closed(), str(r))
        k.close()
        k = Kap(self.port, self.token)
        r = k.handshake(proto="kap/9")
        self.check("refuse.version", r.get("code") == "version" and k.closed(), str(r))
        k.close()
        k = Kap(self.port, self.token)
        k.read()
        t0 = time.time()
        closed = k.closed(8)
        self.check("refuse.handshake-timeout", closed and 4.5 <= time.time() - t0 <= 7.5, f"closed after {time.time() - t0:.1f} s")
        k.close()

    def t_framing(self):
        k = self.kap()
        k.send({"t": "frobnicate", "id": 3})
        r = k.read()
        self.check("frame.unknown-t-nonfatal", r.get("t") == "error" and r.get("code") == "bad_frame" and r.get("id") == 3, str(r))
        res = k.call("ping")
        self.check("frame.still-open", res is not None and res["ok"])
        k.send({"t": "sub", "id": 4, "topic": "nope"})
        r = k.read_until(lambda f: f.get("id") == 4)
        self.check("frame.unknown-topic", r and r["t"] == "res" and r["ok"] is False and r["code"] == "unknown_topic", str(r))
        res = k.call("no_such_op")
        self.check("frame.unknown-op", res and res["ok"] is False and res["code"] == "unknown_op" and res["error"] == "unknown op no_such_op", str(res))
        res = k.call("mod_reload", {"id": "kiln-admin"})
        self.check("frame.op-failed", res and res["ok"] is False and res["code"] == "op_failed", str(res))
        k.send_raw(b"{not json")
        r = k.read()
        self.check("frame.bad-json-fatal", r.get("code") == "bad_frame" and k.closed(), str(r))
        k.close()
        k = self.kap()
        k.send_raw(b"", length=(1 << 20) + 1)
        r = k.read()
        self.check("frame.too-large", r.get("code") == "too_large" and k.closed(), str(r))
        k.close()
        k = self.kap()
        k.send_raw(b"")
        r = k.read()
        self.check("frame.zero-length", r.get("code") == "bad_frame" and k.closed(), str(r))
        k.close()

    def t_ops(self):
        k = self.kap()
        ops = k.ready["ops"]
        same, diff, skipped = 0, [], []
        for op in ops:
            if op in SKIP:
                skipped.append(op)
                continue
            args = REFUSING.get(op, {})
            a = self.bridge.call(op, args)
            b = k.call(op, args)
            if a is None or b is None:
                diff.append(f"{op}: no answer ({'file' if a is None else 'tcp'})")
                continue
            b2 = {x: y for x, y in b.items() if x not in ("t", "id", "code")}
            d = first_diff(normalize(a), normalize(b2))
            if d:
                diff.append(f"{op}: {d}")
            else:
                same += 1
            if self.verbose:
                print(f"  {op}: ok={a.get('ok')} {'' if not d else 'DIFF ' + d}")
            if b.get("ok") is False and b.get("code") not in ("op_failed", "unknown_op"):
                diff.append(f"{op}: code {b.get('code')}")
        self.check("ops.tcp-equals-file", not diff, f"{same} of {len(ops)} ops equal, skipped {len(skipped)} ({', '.join(skipped)})" +
                   ("; " + " | ".join(diff[:8]) if diff else ""))
        # the new ops answer the same way on both transports too (checked above); their shapes:
        info = k.call("admin_info")["data"]
        self.check("ops.admin_info", info["protocol"] == "kap/1" and info["transports"] == ["file", "tcp"] and
                   info["endpoint"]["port"] == self.port and info["connections"] >= 1, json.dumps({x: info[x] for x in info if x != "ops"})[:300])
        ns = k.call("network_status")
        nd = ns.get("data") or {}
        self.check("ops.network_status", ns["ok"] and nd.get("serverId") == self.endpoint["server"]["id"] and "required" in nd and
                   "config" in nd, json.dumps({x: nd.get(x) for x in ("serverId", "serverName", "modpack", "advertise", "required", "pending")})[:400])
        nat = k.call("native_status")
        self.check("ops.native_status", nat["ok"] and "chain" in nat["data"], json.dumps(nat["data"])[:200])
        lt = k.call("log_tail", {"lines": 5})
        lg = k.call("log_tail", {"file": "game", "lines": 3})
        self.check("ops.log_tail", lt["ok"] and len(lt["data"]["lines"]) == 5 and lt["data"]["file"] == "kiln.log" and
                   lg["ok"] and lg["data"]["file"] == "enshrouded_server.log" and 1 <= len(lg["data"]["lines"]) <= 3,
                   f"kiln: {lt['data']['lines'][-1][:80]!r} | game: {lg['data']['lines'][-1][:80]!r}")
        bad = k.call("log_tail", {"file": "../kiln.json"})
        big = k.call("log_tail", {"lines": 5001})
        self.check("ops.log_tail-refuses", bad["ok"] is False and big["ok"] is False, f"{bad['error']} | {big['error']}")
        k.close()

    def t_push(self):
        k = self.kap()
        k.send({"t": "sub", "id": 50, "topic": "state"})
        r = k.read_until(lambda f: f.get("id") == 50)
        times, seqs = [], []
        end = time.time() + 6.5
        while time.time() < end:
            f = k.read_until(lambda f: f.get("t") == "push" and f.get("topic") == "state", 3)
            if f is None:
                break
            times.append(time.time())
            seqs.append(f["seq"])
            data = f["data"]
        # the first push is the cached state sent on sub; the periodic ones follow it at any phase, then about 1 s apart
        gaps = [b - a for a, b in zip(times[1:], times[2:])]
        st = json.loads((self.lab / "shroudforge/exports/emberwatch/state.json").read_text())
        self.check("push.state", r and r["ok"] and len(times) >= 5 and seqs == list(range(1, len(seqs) + 1)) and
                   all(0.6 <= g <= 1.6 for g in gaps) and set(data) == set(st),
                   f"{len(times)} pushes, gaps {', '.join(f'{g:.2f}' for g in gaps)} s, keys {sorted(data)}")
        k.send({"t": "sub", "id": 51, "topic": "state"})
        k.read_until(lambda f: f.get("id") == 51)
        f1 = k.read_until(lambda f: f.get("t") == "push", 3)
        f2 = k.read_until(lambda f: f.get("t") == "push", 3)
        self.check("push.resub-no-duplicates", f1 and f2 and f2["seq"] == f1["seq"] + 1 and f2["seq"] - seqs[-1] >= 1)
        k.send({"t": "unsub", "id": 52, "topic": "state"})
        k.read_until(lambda f: f.get("id") == 52)
        k.pending = []
        late = k.read_until(lambda f: f.get("t") == "push", 2.5)
        self.check("push.unsub", late is None, str(late)[:100])
        # log topic: write a control command that logs, see its lines arrive
        k.send({"t": "sub", "id": 53, "topic": "log"})
        k.read_until(lambda f: f.get("id") == 53)
        k.call("command", {})  # logs "command failed: ..."
        got = k.read_until(lambda f: f.get("t") == "push" and f.get("topic") == "log" and any("command" in l for l in f["data"]["lines"]), 5)
        self.check("push.log", got is not None, json.dumps(got)[:200] if got else "no log push in 5 s")
        k.close()

    def t_ping(self):
        k = self.kap()
        k.send({"t": "ping", "id": 77, "ts": 123})
        r = k.read_until(lambda f: f.get("t") == "pong", 3)
        self.check("ping.pong", r and r["id"] == 77 and r["ts"] == 123, str(r))
        t0 = time.time()
        p = k.read_until(lambda f: f.get("t") == "ping", 20)
        self.check("ping.server-pings", p is not None and 13 <= time.time() - t0 <= 18.5, f"server ping after {time.time() - t0:.1f} s: {p}")
        k.close()

    def t_limits(self):
        conns = [self.kap() for _ in range(4)]
        extra = Kap(self.port, self.token)
        r = extra.read()
        self.check("limits.connections", r.get("t") == "error" and r.get("code") == "busy" and extra.closed(), str(r))
        extra.close()
        for c in conns[1:]:
            c.close()
        k = conns[0]
        # 70 requests at once: at most 64 in flight; the rest are busy at once
        for i in range(70):
            k.send({"t": "req", "id": 1000 + i, "op": "players", "args": {}})
        res = []
        end = time.time() + 15
        while len(res) < 70 and time.time() < end:
            f = k.read_until(lambda f: f.get("t") == "res" and 1000 <= f.get("id", 0) < 1070, 5)
            if f is None:
                break
            res.append(f)
        busy = [f for f in res if f.get("code") == "busy"]
        ok = [f for f in res if f.get("ok")]
        self.check("limits.inflight", len(res) == 70 and len(busy) >= 1 and len(ok) >= 1 and len(busy) + len(ok) == 70,
                   f"{len(ok)} ok, {len(busy)} busy, {len(res)} answers")
        # a duplicate id in flight is refused
        k.send({"t": "req", "id": 2000, "op": "mod_test", "args": {"id": "hello"}})
        k.send({"t": "req", "id": 2000, "op": "ping", "args": {}})
        e = k.read_until(lambda f: f.get("t") == "error" and f.get("id") == 2000, 5)
        self.check("limits.duplicate-id", e is not None and e["code"] == "bad_frame", str(e))
        k.read_until(lambda f: f.get("t") == "res" and f.get("id") == 2000, 120)
        k.close()

    def t_rotate(self):
        a = self.kap()
        b = self.kap()
        old = self.token
        r = a.call("admin_token_rotate")
        bye = b.read_until(lambda f: f.get("t") == "bye", 3)
        new = self.token
        still = a.call("ping")
        self.check("rotate", r["ok"] and r["data"] == {"rotated": True} and bye and bye["reason"] == "token" and b.closed() and
                   new != old and still and still["ok"], f"bye {bye}, token changed {new != old}")
        stale = Kap(self.port, old)
        rr = stale.handshake()
        self.check("rotate.old-token-refused", rr.get("code") == "auth_failed")
        stale.close()
        fresh = self.kap()
        self.check("rotate.new-token-works", fresh.call("ping")["ok"])
        fresh.close()
        a.close()
        b.close()


    # ------------------------------------------------------------------ build (opt-in: edits the lab world, then undoes)
    def t_build(self):
        import base64
        k = self.kap()
        if "build_status" not in k.ready["ops"]:
            self.check("build.ops", False, "no build_* ops (Kiln.Build is not loaded)")
            return
        c = lambda op, a=None: k.call(op, a or {}, timeout=90)
        st = c("build_status")
        g = (st.get("data") or {}).get("grid") or {}
        cs = g.get("cellSize", 0.5)
        self.check("build.status", st["ok"] and st["data"]["write"] and cs > 0, json.dumps({x: st["data"][x] for x in ("read", "write", "props", "persist", "keptChunks")}) + f" grid {g}")
        bases = c("build_bases")
        self.check("build.bases", bases["ok"] and isinstance(bases["data"], list), json.dumps(bases["data"])[:200])
        cell = lambda v: int(v // cs)

        def read(x, y, z, sx, sy, sz):  # world corner (m), size in cells
            r = c("build_read", {"x": cell(x), "y": cell(y), "z": cell(z), "sx": sx, "sy": sy, "sz": sz})
            if not r["ok"]:
                raise RuntimeError(f"build_read: {r.get('error')}")
            return r["data"], base64.b64decode(r["data"]["data"])

        def surface(x, z):
            d, raw = read(x, 0, z, 1, 2000, 1)
            v = struct.unpack(f"<{len(raw) // 2}H", raw)
            top = max((i for i, w in enumerate(v) if w), default=None)
            return None if top is None else (top + 1) * cs

        b0 = bases["data"][0] if bases["data"] else None
        px, pz = (b0["center"]["x"] + b0["half"]["x"] + 90, b0["center"]["z"]) if b0 else (5120.0, 5120.0)
        ground = surface(px, pz)
        self.check("build.read-column", ground is not None, f"surface at ({px}, {pz}): y {ground}")
        if ground is None:
            return
        P = {"x": px + 0.25, "y": ground + 12.25, "z": pz + 0.25}
        Q = {"x": P["x"] + 12, "y": P["y"], "z": P["z"]}
        n = int(6 / cs)  # 6 m boxes around P and Q
        around = lambda p: (p["x"] - 3, p["y"] - 3, p["z"] - 3, n, n, n)
        before_p, before_q = read(*around(P))[1], read(*around(Q))[1]
        big = c("build_read", {"x": 0, "y": 0, "z": 0, "sx": 41, "sy": 40, "sz": 40})
        self.check("build.read-limit", big["ok"] is False and "65536" in big["error"], big.get("error", ""))
        if b0:
            inside = {"edit": "add", "shape": "box", "x": b0["center"]["x"], "y": b0["center"]["y"] + 20, "z": b0["center"]["z"], "hx": 1, "material": 2}
            r1 = c("build_shape", inside)
            r2 = c("build_shape", inside | {"flags": ["InBases", "DryRun"]})
            self.check("build.shape-base-refused", r1["ok"] and r1["data"]["refusal"] == "Base" and r1["data"]["cellsChanged"] == 0 and
                       r2["ok"] and r2["data"]["refusal"] == "DryRun", f"no flags: {r1['data'].get('refusal')}, InBases+DryRun: {r2['data'].get('refusal')} ({r2['data'].get('cellsChanged')} cells)")
        add = c("build_shape", {"edit": "add", "shape": "box", **P, "hx": 1, "material": 2})
        after = read(*around(P))
        self.check("build.shape-add", add["ok"] and add["data"]["cellsChanged"] > 0 and add["data"]["refusal"] == "None" and
                   after[0]["solid"] > 0 and after[1] != before_p, json.dumps(add.get("data"))[:240] + f"; read back {after[0]['solid']} solid")
        bad = [c("build_shape", {"edit": "melt", **P, "hx": 1}), c("build_shape", {"edit": "add", **P, "hx": 100, "material": 2}),
               c("build_shape", {"edit": "add", **P, "hx": 1, "material": 2, "flags": ["Bogus"]})]
        self.check("build.shape-refuses", all(r["ok"] is False for r in bad), " | ".join(r.get("error", "") for r in bad))
        top = {"x": P["x"], "y": P["y"] + 1, "z": P["z"]}
        place = c("build_prop_place", {"template": "Prop_Decoration_T1_Storage_24", **top, "yaw": 45})
        ent = (place.get("data") or {}).get("entity")
        box = {"min": {x: v - 3 for x, v in P.items()}, "max": {x: v + 3 for x, v in P.items()}}
        props = c("build_props", box)
        self.check("build.prop-place", place["ok"] and ent and props["ok"] and any(p["entity"] == ent for p in props["data"]),
                   json.dumps(place.get("data") or place.get("error"))[:240] + f"; {len(props.get('data') or [])} prop(s) near")
        name = f"kaptest-{os.getpid()}"
        cap = c("build_capture", {"name": name, **box})
        lst = c("build_schematics")
        self.check("build.capture", cap["ok"] and cap["data"]["props"] >= 1 and any(s["name"] == name for s in lst["data"]),
                   json.dumps(cap.get("data") or cap.get("error"))[:240])
        badname = c("build_capture", {"name": "../x", **box})
        self.check("build.capture-name-refused", badname["ok"] is False, badname.get("error", ""))
        paste = c("build_paste", {"name": name, **Q, "rotation": 90})
        pasted = read(*around(Q))
        self.check("build.paste", paste["ok"] and paste["data"]["ok"] and paste["data"]["cells"]["cellsChanged"] > 0 and
                   paste["data"]["propsPlaced"] >= 1 and pasted[1] != before_q, json.dumps(paste.get("data") or paste.get("error"))[:300])
        hist = c("build_history", {"owner": "admin"})
        alls = c("build_history")
        self.check("build.history", hist["ok"] and len(hist["data"]) >= 2 and all(h["owner"] == "kiln-admin" for h in hist["data"]) and
                   len(alls["data"]) >= len(hist["data"]), "; ".join(f"{h['id']} {h['label']} {h['kind']} {h['cells']} cells" for h in hist["data"]))
        u1 = c("build_undo")
        self.check("build.undo-paste", u1["ok"] and "paste" in u1["data"]["label"] and u1["data"]["propsRemoved"] >= 1 and
                   read(*around(Q))[1] == before_q, json.dumps(u1.get("data") or u1.get("error")))
        rm = c("build_prop_remove", {"entity": ent})
        rm2 = c("build_prop_remove", {"entity": ent})
        self.check("build.prop-remove", rm["ok"] and rm["data"]["removed"] and not (rm2["ok"] and rm2["data"]["removed"]),
                   f"{rm.get('data') or rm.get('error')} then {rm2.get('data') or rm2.get('error')}")
        undone = []
        for _ in range(10):
            u = c("build_undo")
            if not u["ok"]:
                break
            undone.append(u["data"]["label"])
        self.check("build.undo-all", read(*around(P))[1] == before_p and not c("build_history", {"owner": "admin"})["data"],
                   f"undid {undone}; P reads as before")
        nobody = c("build_undo", {"owner": "kaptest-nobody"})
        self.check("build.undo-nothing", nobody["ok"] is False, nobody.get("error", ""))
        po = c("build_placeonce_list")
        other = c("build_placeonce_forget", {"mod": "hello", "key": "x"})
        own = c("build_placeonce_forget", {"key": "kaptest-none"})
        self.check("build.placeonce", po["ok"] and isinstance(po["data"], list) and other["ok"] is False and own["ok"] and
                   own["data"]["forgotten"] is False, f"{len(po['data'])} record(s); other mod: {other.get('error')}")
        reg = c("build_regions")
        rel = c("build_release", {"box": {"x": cell(P["x"] - 3), "y": cell(P["y"] - 3), "z": cell(Q["z"] - 3), "sx": int(20 / cs), "sy": n, "sz": n}})
        reg2 = c("build_regions")
        self.check("build.regions-release", reg["ok"] and rel["ok"] and reg2["data"]["keptChunks"] <= reg["data"]["keptChunks"],
                   f"kept {reg['data']['keptChunks']} -> released {rel.get('data') or rel.get('error')} -> kept {reg2['data']['keptChunks']}")
        f = self.lab / "kiln/data/kiln.build/schematics" / f"{name}.kschem"
        if f.exists():
            f.unlink()
        k.close()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lab", required=True, type=Path)
    ap.add_argument("--only", default="")
    ap.add_argument("--verbose", action="store_true")
    a = ap.parse_args()
    s = Suite(a.lab.expanduser(), a.verbose)
    order = ["vector", "endpoint", "handshake", "refused", "framing", "ops", "push", "ping", "limits", "rotate"]
    only = [x for x in a.only.split(",") if x]
    for name in order + ["build"]:
        if name == "build" and name not in only:
            continue
        if only and name not in only:
            continue
        try:
            getattr(s, "t_" + name)()
        except Exception as e:
            s.check(name, False, f"{type(e).__name__}: {e}")
    print(f"kaptest: {s.passed} PASS, {s.failed} FAIL", flush=True)
    sys.exit(1 if s.failed else 0)


if __name__ == "__main__":
    main()
