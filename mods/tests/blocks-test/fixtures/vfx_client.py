#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A fake Kiln client for what a mod's VFX blocks send: joins a lab running blocks-test (which needs "labPort" in kiln/config/network.json), and checks that a client
without the mod is refused, that the content sync carries the references, and that an effect's VFX with parameters is relayed on kiln:vfx. No game client, no VFX.

    mods/tests/blocks-test/fixtures/vfx_client.py --port <labPort> --lab <lab dir> [--no-vfx-client]

Prints PASS/FAIL lines; exits non-zero on any FAIL. Run it after blocks-test logged "VFX checks done".
"""
import argparse
import os
import struct
import sys
import time
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [os.path.join(HERE, "..", "..", "..", "..", "tools", "nettest"), os.path.join(HERE, "..", "..", "sync-test")]
import nettest as nt  # noqa: E402
import synctest  # noqa: E402

VFX, VERSION = "kiln:vfx", "1"
MSG_PLAY = 1
SAMPLE = "frostbolt-block"
NS = uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77")
FLOAT, FLOAT3, COLOR, INT = 0x90b5085d, 0x3afc452a, 0x53039620, 0x5655a2c9
fails = 0


def check(ok, what):
    global fails
    print(("PASS " if ok else "FAIL ") + what, flush=True)
    fails += not ok
    return ok


def fnv(s):
    h = 0x811c9dc5
    for b in s.encode():
        h = ((h ^ b) * 0x01000193) & 0xffffffff
    return h


def block_id(kind, name):
    return str(uuid.uuid5(NS, f"{SAMPLE}:{kind}:{name}"))


def console(lab, cmd):
    d = os.path.join(lab, "kiln", "control", "console")
    os.makedirs(d, exist_ok=True)
    tmp = os.path.join(d, f".vfx-{os.getpid()}.tmp")
    with open(tmp, "w") as f:
        f.write(cmd + "\n")
    os.rename(tmp, os.path.join(d, f"vfx-{int(time.time() * 1000)}.cmd"))


class Client(synctest.SyncClient):
    def set_table(self, table):
        self.table = table
        wanted = dict(synctest.OURS, **{VFX: VERSION})
        self.channels = {n: i for i, n, v, _ in table if wanted.get(n) == v}


def play(body):
    r = nt.R(body)
    m = {"kind": r.u8(), "guid": uuid.UUID(bytes_le=bytes(r.take(16)))}
    m["at"] = struct.unpack("<3d", r.take(24))
    r.take(16 + 4 + 4 + 1 + 12 + 12 + 16)
    m["life"], m["token"] = struct.unpack("<q", r.take(8))[0], r.u32()
    params = {}
    for _ in range(r.u8()):
        h, t = r.u32(), r.u32()
        params[h] = (t, r.blob())
    m["params"] = params
    return m


def refused_without_the_mod(port):
    without = [m for m in nt.FULL_MODS if m[0] != SAMPLE]
    c = Client(port, "vfx-no-mod", channels=[(VFX, VERSION, False)])
    c.hello(without, c.declared)
    c.wait(lambda: c.welcome is not None or c.reject is not None, 10)
    text = " ".join(str(x) for x in (c.reject or []))
    check(c.reject is not None and SAMPLE in text, f"a Kiln client without the mod is refused: a new VFX has clients mod: {c.reject}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--lab", required=True)
    ap.add_argument("--no-vfx-client", action="store_true", help="the lab has data/content/no-vfx-client: the data names no VFX")
    a = ap.parse_args()
    lab = os.path.expanduser(a.lab)
    nt.probe_server(a.port)
    check(any(m[0] == SAMPLE for m in nt.FULL_MODS), f"the server lists {SAMPLE}, as a mod its clients need")
    refused_without_the_mod(a.port)

    channels = synctest.CHANNELS + [(VFX, VERSION, False)]
    c = Client(a.port, "vfx-client", channels=channels)
    check(c.connect(nt.FULL_MODS) and synctest.CONTENT in c.channels, "a client with the mod joins, and has the content channel")
    if not c.channels.get(synctest.CONTENT):
        return 1
    c.send(nt.LAB_READY)
    c.wait(lambda: any("doc" in s for s in c.contents), 20)
    state = next((s for s in c.contents if "doc" in s), None)
    if not check(state is not None, f"the server sent its content state: {[s.get('reason') for s in c.contents]}"):
        return 1
    doc = state["doc"]
    templates = doc.get("templates", {})
    adds = {x["name"]: x for x in templates.get("add", [])}
    trail, burst, charge, aura = (block_id("vfx", n) for n in ("frost_trail", "frost_burst", "frost_charge", "frost_aura"))
    empty = "none"

    def value(section, entry, path):
        return next((v[path] for k, v in doc.get(section, {}).items() if isinstance(v, dict) and v.get("@name") == entry and path in v), None)

    flight = adds.get(f"{SAMPLE}.frostbolt.projectile")
    check(flight is not None, "the projectile's template is in the definitions")
    carrier = next((x for n, x in adds.items() if "frost_burst" in n and ".vfx." in n), None)
    check(carrier is not None, f"the carrier is in the definitions: {carrier and carrier['name']}")
    if carrier:
        check(carrier["base"] == "VFX_NPC_Place", f"it starts from the game's own pattern: {carrier['base']}")

    wanted = {
        "flight": ("templates", f"{SAMPLE}.frostbolt.projectile", "VfxComponentResource.vfx", trail),
        "hit": ("templates", carrier["name"] if carrier else "", "VfxComponentResource.vfx", burst),
        "cast": ("sequences", f"{SAMPLE}.frostbolt.cast", "events[VfxNotifierEvent#0].vfx", charge),
    }
    for what, (section, entry, path, guid) in wanted.items():
        have = value(section, entry, path)
        if a.no_vfx_client:
            check(have in (None, empty, "00000000-0000-0000-0000-000000000000"), f"{what}: this side names no VFX: {have}")
        else:
            check(have == guid, f"{what}: the sync carries the reference: {have}")
    rows = value("buffs", f"{SAMPLE}.chilled", "whileApplied")
    check(isinstance(rows, list) and len(rows) == 1, f"status: the buff's list goes whole: {rows}")
    if rows and not a.no_vfx_client:
        check(rows[0].get("vfx") == aura, "and names the aura")
        check(rows[0].get("offset.orientationOffset.w") == 1, "with an identity rotation, which the receiver's new row needs")
    c.ack_content(state["id"], state["fields"])

    if not a.no_vfx_client and check(VFX in c.channels, f"the server has {VFX}"):
        c.messages.clear()
        console(lab, "blockstest vfxcue")
        m = c.take(VFX, MSG_PLAY, 15)
        if check(m is not None, "an effect with parameters reaches a client as a relayed play"):
            p = play(m)
            print(f"INFO {p}")
            check(str(p["guid"]).startswith("b0b78a7d"), f"of the game's VFX: {p['guid']}")
            pp = p["params"]
            check(pp.get(fnv("radius")) == (FLOAT, struct.pack("<f", 3.0)), "radius 3, a float")
            check(pp.get(fnv("color")) == (COLOR, bytes([255, 0, 0, 255])), "colour #ff0000")
            check(pp.get(fnv("targetPosition")) == (FLOAT3, struct.pack("<3f", 1, 2, 3)), "targetPosition [1, 2, 3]")
            check(pp.get(fnv("count")) == (INT, struct.pack("<i", 7)), "count 7, a whole number")
            check(p["at"] != (0.0, 0.0, 0.0), f"where the target is: {p['at']}")

    nt.probe_server(a.port)
    p = Client(a.port, "vfx-after", channels=channels)
    check(p.connect(nt.FULL_MODS), "the server is still up after all of it")
    print(f"{fails} FAIL")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
