#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Fake Kiln clients for the server side of Kiln's server-to-client sync (docs/kiln/sync.md, "Tests").

Needs a lab server with "labPort" in kiln/config/network.json and the mods sync-test (+ bigstacks) loaded:

    mods/tests/sync-test/synctest.py --port 25901 --lab ~/srv/enshrouded/labs/sync [--only NAME,...] [--verbose]

Speaks the kiln:content v1 / kiln:config v1 channels like a Kiln client and drives sync-test:ctl. Uses the protocol
client of tools/nettest/nettest.py. Prints PASS/FAIL lines; exits non-zero on any FAIL.
"""
import argparse
import json
import os
import sys
import time
import zlib

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "tools", "nettest"))
import nettest as nt  # noqa: E402

CONTENT, CONFIG, CTL = "kiln:content", "kiln:config", "sync-test:ctl"
OURS = {CONTENT: "1", CONFIG: "1", CTL: "1", "net-test:echo": "1"}
CHANNELS = [(CONTENT, "1", False), (CONFIG, "1", False), (CTL, "1", False)]
WOOD, RESIN, TORCH = "0x2b0cf1d5", "0xf4f57bb1", "0x7cd80a04"
TWIGS = "0x6b356a28"


def fnv(data):
    h = 2166136261
    for b in data:
        h = ((h ^ b) * 16777619) & 0xFFFFFFFF
    return h or 1


class SyncClient(nt.Client):
    def __init__(self, port, name, verbose=False, channels=None):
        super().__init__(port, name, verbose)
        self.parts = {}       # state id -> {part: bytes}
        self.contents = []    # complete states: dict(id, gen, raw, fields, doc, compressed, parts) or dict(id, reason)
        self.configs = []     # config states: dict(id, items={id: (mod, kind, values)})
        self.replies = []
        self.declared = channels if channels is not None else CHANNELS

    def set_table(self, table):
        self.table = table
        self.channels = {n: i for i, n, v, _ in table if OURS.get(n) == v}

    def connect(self, mods):
        self.hello(mods, self.declared)
        return self.wait(lambda: self.welcome is not None, 8)

    def pump(self, seconds):
        super().pump(seconds)
        self._drain()

    def _drain(self):
        keep = []
        for cid, mid, body in self.messages:
            if cid == self.channels.get(CONTENT) and mid == 0:
                self._content(body)
            elif cid == self.channels.get(CONFIG) and mid == 0:
                self._config(body)
            elif cid == self.channels.get(CTL) and mid == 1:
                self.replies.append(nt.R(body).string())
            else:
                keep.append((cid, mid, body))
        self.messages = keep

    def _content(self, body):
        r = nt.R(body)
        fmt, sid, gen, raw_len, fields, part, parts, codec = r.u16(), r.u32(), r.u64(), r.u32(), r.u32(), r.u16(), r.u16(), r.u8()
        data = r.blob()
        if parts == 0:
            self.contents.append(dict(id=sid, reason=data.decode(), fields=fields, raw_len=raw_len))
            return
        got = self.parts.setdefault(sid, {})
        got[part] = data
        if len(got) < parts:
            return
        packed = b"".join(got[i] for i in range(parts))
        del self.parts[sid]
        raw = zlib.decompress(packed, -15)
        self.contents.append(dict(id=sid, gen=gen, raw=raw, raw_len=raw_len, fields=fields, fmt=fmt, codec=codec,
                                  compressed=len(packed), parts=parts, doc=json.loads(raw)))

    def _config(self, body):
        r = nt.R(body)
        fmt, sid, n = r.u16(), r.u32(), r.varu()
        items = {}
        for _ in range(n):
            cid, mod, kind, values = r.string(), r.string(), r.u8(), r.string()
            items[cid] = (mod, kind, json.loads(values))
        self.configs.append(dict(id=sid, fmt=fmt, items=items))

    def control(self, cmd, seconds=5):
        self.replies = []
        self.data(CTL, 0, nt.W().string(cmd).b)
        self.wait(lambda: self.replies, seconds)
        return self.replies[0] if self.replies else None

    def data_padded(self, chan, msg, body):
        """Like data(), but every frame zero-padded to a multiple of 4 bytes, as kiln.net's game transport sends them."""
        cid = self.channels[chan]
        pad = lambda b: bytes(b) + b"\0" * (-len(b) % 4)
        if nt.DATA_HDR + len(body) <= nt.MAX_FRAME:
            self.send(pad(nt.frame(nt.T_DATA).u16(cid).u16(msg).raw(body).b))
            return
        self.seq += 1
        chunk0 = nt.MAX_FRAME - nt.FRAG_HDR - 6
        chunk = nt.MAX_FRAME - nt.FRAG_HDR
        count = 1 + (len(body) - chunk0 + chunk - 1) // chunk
        off = 0
        for i in range(count):
            n = min(chunk0 if i == 0 else chunk, len(body) - off)
            w = nt.frame(nt.T_FRAG).u16(cid).u32(self.seq).u16(i).u16(count)
            if i == 0:
                w.u16(msg).u32(len(body))
            w.raw(body[off:off + n])
            off += n
            self.send(pad(w.b))

    def ack_content(self, sid, applied, detail="synctest"):
        self.data(CONTENT, 1, nt.W().u32(sid).u8(0).varu(applied).varu(0).varu(0).varu(3).string(detail).b)

    def ack_config(self, sid, applied, detail="synctest"):
        self.data(CONFIG, 1, nt.W().u32(sid).varu(applied).varu(0).string(detail).b)


class Suite(nt.Suite):
    def __init__(self, port, verbose, lab):
        super().__init__(port, verbose, lab)
        self.mods = []

    def log_has(self, text, since, seconds=5):
        if not self.lab:
            return None
        path = os.path.join(self.lab, "kiln", "logs", "kiln.log")
        end = time.time() + seconds
        while time.time() < end:
            with open(path, "rb") as f:
                f.seek(since)
                if text.encode() in f.read():
                    return True
            time.sleep(0.2)
        return False

    def log_size(self):
        return os.path.getsize(os.path.join(self.lab, "kiln", "logs", "kiln.log")) if self.lab else 0

    def client(self, name, channels=None):
        c = SyncClient(self.port, name, self.verbose, channels)
        ok = c.connect(self.mods)
        if not ok:
            self.check(f"{name}.welcome", False, "no WELCOME in 8 s")
        return c

    def run(self):
        probe = SyncClient(self.port, "probe")
        probe.hello([], CHANNELS)
        probe.wait(lambda: probe.welcome is not None, 8)
        if not probe.welcome:
            self.check("handshake", False, "no WELCOME (is the lab up with labPort set?)")
            raise SystemExit(2)
        self.mods = [(m, v, s) for m, v, s in probe.welcome["mods"]]
        names = {n: v for _, n, v, _ in probe.welcome["table"]}
        self.check("handshake.channels", names.get(CONTENT) == "1" and names.get(CONFIG) == "1" and names.get(CTL) == "1",
                   str(sorted(names.items())))
        self.check("handshake.not-required", all(not req for _, n, _, req in probe.welcome["table"] if n in (CONTENT, CONFIG)),
                   "kiln:content / kiln:config must not be required (older Kiln clients join without them)")

        # ---- content at join ----
        a = self.client("sync-a")
        a.wait(lambda: a.contents, 10)
        st = a.contents[0] if a.contents else None
        self.check("content.at-join", st is not None and "doc" in st, f"{len(a.contents)} state(s)")
        if not st or "doc" not in st:
            return
        doc = st["doc"]
        self.check("content.integrity", len(st["raw"]) == st["raw_len"] and fnv(st["raw"]) == st["id"] and st["fmt"] == 1,
                   f"id {st['id']:08x}, {st['raw_len']} bytes -> {st['compressed']} deflate in {st['parts']} part(s)")
        items, recipes = doc.get("items", {}), doc.get("recipes", {})
        self.check("content.fields", doc.get("@fields") == st["fields"] and st["fields"] >= 3,
                   f"@fields {doc.get('@fields')}, header {st['fields']}, items {len(items)}, recipes {len(recipes)}")
        wood_srv = a.control(f"get-item Material_T1_Wood_Logs maxStackSize")
        resin_srv = a.control(f"get-item Material_T1_Resin maxStackSize")
        wood = items.get(WOOD, {}).get("maxStackSize")
        resin = items.get(RESIN, {}).get("maxStackSize")
        self.check("content.wood-as-server", wood_srv is not None and wood is not None and float(wood_srv.split()[1]) == wood,
                   f"document {wood}, server {wood_srv} (BigStacks x4: 1000)")
        self.check("content.resin-as-server", resin_srv is not None and resin is not None and float(resin_srv.split()[1]) == resin,
                   f"document {resin}, server {resin_srv}")
        self.check("content.names", items.get(WOOD, {}).get("@name") == "Material_T1_Wood_Logs", str(items.get(WOOD)))
        torch = recipes.get(TORCH, {})
        self.check("content.recipe-list", torch.get("inputs") == [{"item": TWIGS, "count": 3}, {"item": WOOD, "count": 2}] and
                   torch.get("outputs") == [{"item": "0xcf128197", "count": 2}], json.dumps(torch))
        rod = recipes.get("0x9cd203fa", {})
        self.check("content.recipe-field", rod.get("craftingDuration") == 2, json.dumps(rod))
        self.check("content.no-drops-loot", "drops" not in doc and "loot" not in doc, ", ".join(doc.keys()))
        since = self.log_size()
        a.ack_content(st["id"], st["fields"])
        self.check("content.ack-logged", self.log_has(f"content state {st['id']:08x} applied", since) is not False)

        # ---- the sync format round trip on the server's own registries ----
        r = a.control("roundtrip", 15)
        self.check("content.roundtrip", r is not None and r.startswith("ok"), r or "no reply")

        r = a.control("config-roundtrip", 10)
        self.check("config.client-side-roundtrip", r is not None and r.startswith("ok"), r or "no reply")

        # ---- configs at join ----
        a.wait(lambda: a.configs, 5)
        cs = a.configs[-1] if a.configs else None
        self.check("config.at-join", cs is not None, f"{len(a.configs)} state(s)")
        if cs:
            srv = cs["items"].get("sync-test-server")
            vals = srv[2] if srv else {}
            self.check("config.server-synced", srv is not None and vals.get("maxHomes") == 7 and "rules" in vals and
                       vals["rules"].get("cooldown") == "30s" and vals.get("mode") == "Normal", json.dumps(vals))
            self.check("config.not-synced-absent", "motd" not in vals, json.dumps(vals))
            com = cs["items"].get("sync-test")
            self.check("config.common-synced", com is not None and set(com[2].keys()) == {"feature"}, json.dumps(com and com[2]))
            self.check("config.client-absent", "sync-test-client" not in cs["items"] and "sync" not in cs["items"],
                       ", ".join(cs["items"].keys()))
            since = self.log_size()
            a.ack_config(cs["id"], 7)
            self.check("config.ack-logged", self.log_has(f"config state {cs['id']:08x}: 7 value(s) applied", since) is not False)

        # ---- config change -> pushed ----
        n = len(a.configs)
        t0 = time.time()
        rep = a.control("set-config maxHomes 9")
        a.wait(lambda: len(a.configs) > n, 5)
        new = a.configs[-1] if len(a.configs) > n else None
        self.check("config.change-pushed", new is not None and new["items"]["sync-test-server"][2].get("maxHomes") == 9 and
                   new["id"] != cs["id"], f"{rep}; {time.time() - t0:.1f} s; {json.dumps(new and new['items']['sync-test-server'][2])}")
        n = len(a.configs)
        a.control("set-config motd hello there")
        a.pump(3)
        self.check("config.unsynced-change-not-pushed", len(a.configs) == n, f"{len(a.configs) - n} state(s)")
        a.control("set-config maxHomes 7")

        # ---- content changes -> pushed ----
        n = len(a.contents)
        t0 = time.time()
        rep = a.control("set-recipe Tool_Torch01_T0_PK craftingDuration 7")
        a.wait(lambda: len(a.contents) > n, 6)
        new = a.contents[-1] if len(a.contents) > n else None
        self.check("content.new-field-pushed", new is not None and new.get("doc", {}).get("recipes", {}).get(TORCH, {}).get("craftingDuration") == 7,
                   f"{rep}; {time.time() - t0:.1f} s")
        n = len(a.contents)
        t0 = time.time()
        rep = a.control("set-item Material_T1_Resin maxStackSize 555")
        a.wait(lambda: len(a.contents) > n, 30)  # every 10 s, but an autosave at low priority can hold the ticks for 10+ s
        new = a.contents[-1] if len(a.contents) > n else None
        self.check("content.value-change-pushed", new is not None and new.get("doc", {}).get("items", {}).get(RESIN, {}).get("maxStackSize") == 555,
                   f"{rep}; {time.time() - t0:.1f} s (value-only changes: periodic export, every 10 s)")
        last = new

        # ---- a late joiner gets the current state ----
        b = self.client("sync-b")
        b.wait(lambda: b.contents and b.configs, 10)
        self.check("late-join.current", b.contents and last and b.contents[-1]["id"] == last["id"] and b.configs and
                   b.configs[-1]["items"]["sync-test-server"][2].get("maxHomes") == 7,
                   f"content {b.contents[-1]['id'] if b.contents else None} vs {last['id'] if last else None}")

        # ---- no kiln channels: nothing sent ----
        since = self.log_size()
        v = self.client("sync-nochan", channels=[(CTL, "1", False)])
        v.pump(3)
        self.check("no-channel.nothing-sent", not v.contents and not v.configs and not v.messages,
                   f"{len(v.contents)} content, {len(v.configs)} config, {len(v.messages)} other message(s)")

        # ---- size cap ----
        n = len(a.contents)
        rep = a.control("set-sync maxContentKiB 16")
        a.wait(lambda: len(a.contents) > n, 6)
        new = a.contents[-1] if len(a.contents) > n else None
        self.check("cap.not-sent", new is not None and "reason" in new and "maxContentKiB" in new["reason"],
                   f"{rep}; {new and new.get('reason')}")
        n = len(a.contents)
        a.control("set-sync maxContentKiB 8192")
        a.wait(lambda: len(a.contents) > n, 6)
        new = a.contents[-1] if len(a.contents) > n else None
        self.check("cap.restored", new is not None and "doc" in new, str(new and new.get("fields")))

        # ---- revert the test's code layers -> pushed, then back ----
        n = len(a.contents)
        a.control("revert-content")
        a.wait(lambda: len(a.contents) > n, 6)
        new = a.contents[-1] if len(a.contents) > n else None
        self.check("content.revert-pushed", new is not None and TORCH not in new.get("doc", {}).get("recipes", {}),
                   json.dumps(new and new.get("doc", {}).get("recipes", {}).get(TORCH)))
        a.control("apply-content")

        # ---- fragments with kiln.net's frame padding (the game transport pads every frame to 4 bytes) ----
        for size in (101, 7990, 20000, 20001, 50003):
            data = os.urandom(size)
            body = nt.W().blob(data).b
            a.replies = []
            a.data_padded(CTL, 2, body)
            a.wait(lambda: a.replies, 6)
            want = f"blob {size} {fnv(data)}"
            self.check(f"fragments.padded-{size}", a.replies and a.replies[0] == want, f"{a.replies[:1]} (want {want})")

        # ---- robustness ----
        cid = a.channels[CONTENT]
        for junk in (b"", b"\x01", b"\xff" * 40):
            a.send(nt.frame(nt.T_DATA).u16(cid).u16(1).raw(junk).b)
            a.send(nt.frame(nt.T_DATA).u16(a.channels[CONFIG]).u16(1).raw(junk).b)
            a.send(nt.frame(nt.T_DATA).u16(cid).u16(0).raw(junk).b)  # a to-client message sent to the server
        r = a.control("status")
        self.check("robust.bad-acks", r is not None and r.startswith("content"), r or "no reply")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=25901)
    ap.add_argument("--verbose", action="store_true")
    ap.add_argument("--lab", help="lab server dir (log checks)")
    a = ap.parse_args()
    s = Suite(a.port, a.verbose, a.lab)
    try:
        s.run()
    except SystemExit:
        raise
    except Exception as e:  # a crash is a FAIL
        import traceback
        traceback.print_exc()
        s.check("synctest", False, f"{type(e).__name__}: {e}")
    print(f"synctest: {s.passed} PASS, {s.failed} FAIL", flush=True)
    sys.exit(1 if s.failed else 0)


if __name__ == "__main__":
    main()
