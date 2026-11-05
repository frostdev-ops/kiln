#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Decodes what blocks-test dumped of a VFX block's generated data with tools/kfc: each resource must read as the game's type, and name what the block says.

    mods/tests/blocks-test/fixtures/decode_generated.py --lab <lab dir>

Reads the game's reflection from the lab's server exe. Prints PASS/FAIL lines; exits non-zero on any FAIL.
"""
import argparse
import os
import sys
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[3] / "tools" / "kfc"))
from kfc.blob import Decoder  # noqa: E402
from kfc import cli  # noqa: E402

NS = uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77")
SAMPLE = "frostbolt-block"
fails = 0


def check(ok, what):
    global fails
    print(("PASS " if ok else "FAIL ") + what, flush=True)
    fails += not ok
    return ok


def vfx(name):
    return str(uuid.uuid5(NS, f"{SAMPLE}:vfx:{name}"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lab", required=True)
    a = ap.parse_args()
    lab = Path(os.path.expanduser(a.lab))
    args = argparse.Namespace(game=str(lab), exe=str(lab / "enshrouded_server.exe"))
    c = cli.Ctx(args)
    dec = Decoder(c.types)
    dump = lab / "kiln" / "data" / "blocks-test" / "vfx-resources"
    files = sorted(dump.glob("*.bin"))
    check(len(files) == 3, f"three resources dumped: {[f.name for f in files]}")
    seen = {}
    for f in files:
        guid, type_name, _ = f.name.split(".")
        type_name = type_name.replace("_", "::")
        v = dec.decode(c.type_hash(type_name), f.read_bytes())
        check(True, f"{guid} decodes as {type_name}")
        seen[guid] = v
    templates = {v["name"]: v for v in seen.values() if "components" in v}

    def component(v, name):
        return next((x["value"] for x in v["components"] if x["$type"].endswith("::" + name)), None)

    flight = templates.get(f"{SAMPLE}.frostbolt.projectile")
    if check(flight is not None, "the projectile's template is there"):
        check(component(flight, "VfxComponentResource")["vfx"] == vfx("frost_trail"), f"it names frost_trail: {component(flight, 'VfxComponentResource')['vfx']}")
    carrier = next((v for n, v in templates.items() if ".vfx.frost_burst" in n), None)
    if check(carrier is not None, f"the carrier is there: {list(templates)}"):
        have = sorted(x["$type"].split("::")[-1] for x in carrier["components"])
        check(have == ["CurrentTransform", "DoNotSave", "LifeTime", "RenderTransform", "SpawnTime", "StaticTransform", "VfxComponent", "VfxComponentResource"],
              f"it is the game's pattern, and no more: {have}")
        check(component(carrier, "VfxComponentResource")["vfx"] == vfx("frost_burst"), "it names frost_burst")
        check(component(carrier, "LifeTime")["lifeTime"]["value"] == 1_000_000_000, f"it lives 1 s: {component(carrier, 'LifeTime')['lifeTime']}")
    sequence = next((v for v in seen.values() if "components" not in v), None)
    if check(sequence is not None, "the cast's sequence is there"):
        subs = sequence.get("subSequences") or sequence.get("sequences") or []
        events = [e for s in subs for e in (s.get("events") or []) if e.get("$type", "").endswith("VfxNotifierEvent")]
        event = (events[0].get("value", events[0])) if events else None
        if check(event is not None, f"with its VFX event ({len(events)})"):
            check(event["vfx"] == vfx("frost_charge"), f"it names frost_charge: {event['vfx']}")
            check(event["slot"] == "HandR" and event["attach"] is True, f"at the hand, following it: {event['slot']}, {event['attach']}")
    print(f"{fails} FAIL")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
