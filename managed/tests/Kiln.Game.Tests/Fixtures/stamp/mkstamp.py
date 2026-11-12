#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes the three made-up schema-2 dumps of the layout-stamp tests (no game data: every name is invented).

    python3 managed/tests/Kiln.Game.Tests/Fixtures/stamp/mkstamp.py managed/tests/Kiln.Game.Tests/Fixtures/stamp

a.json  what the fixture mod (StampMod) is compiled against.
b.json  an unrelated field moved: Lamp.spare goes to offset 0, every other field of Lamp moves by 4; the fields the
        mod uses keep their names and types.
c.json  a field the mod uses changed type: Lamp.glow is a float64 instead of a float32.
All three also hold types the mod never names (Torch, Glimmer_Hue), which must not appear in its stamp.
"""
import json
import os
import sys


def fnv(s):
    h = 0x811C9DC5
    for b in s.encode():
        h = ((h ^ b) * 0x01000193) & 0xFFFFFFFF
    return f"0x{h:08X}"


def dump(variant):
    types = []

    def t(qn, kind, size, align, inner=None, fields=None, values=None):
        e = {"name": qn.split("::")[-1] if "<" not in qn else qn[qn.rindex("::", 0, qn.index("<")) + 2:],
             "qualifiedName": qn, "hash": fnv(qn), "kind": kind, "size": size, "align": align}
        if inner:
            e["inner"] = inner
        if fields is not None:
            e["fields"] = [{"name": n, "offset": o, "type": ty} for n, o, ty in fields]
        if values is not None:
            e["values"] = [{"name": n, "value": v} for n, v in values]
        types.append(e)

    for qn, k, s in [("keen::uint8", 2, 1), ("keen::uint16", 4, 2), ("keen::uint32", 6, 4),
                     ("keen::float32", 10, 4), ("keen::float64", 11, 8)]:
        t(qn, k, s, s)
    t("keen::ecs::Component", 18, 1, 1, fields=[])
    t("keen::glimmer::Mode", 12, 1, 1, "keen::uint8", values=[("Off", 0), ("Dim", 1), ("Bright", 2)])
    t("keen::glimmer::Hue", 12, 1, 1, "keen::uint8", values=[("Amber", 0), ("Blue", 1)])
    t("keen::Bitmask8<keen::glimmer::Mode>", 13, 1, 1, "keen::glimmer::Mode")
    t("keen::LampCore", 18, 8, 4, fields=[("heat", 0, "keen::float32"), ("ticks", 4, "keen::uint32")])
    if variant == "a":
        lamp = (20, 4, [("glow", 0, "keen::float32"), ("wick", 4, "keen::uint32"), ("mode", 8, "keen::glimmer::Mode"),
                        ("flags", 9, "keen::Bitmask8<keen::glimmer::Mode>"), ("spare", 10, "keen::uint16"),
                        ("core", 12, "keen::LampCore")])
    elif variant == "b":
        lamp = (24, 4, [("spare", 0, "keen::uint16"), ("glow", 4, "keen::float32"), ("wick", 8, "keen::uint32"),
                        ("mode", 12, "keen::glimmer::Mode"), ("flags", 13, "keen::Bitmask8<keen::glimmer::Mode>"),
                        ("core", 16, "keen::LampCore")])
    else:
        lamp = (24, 8, [("glow", 0, "keen::float64"), ("wick", 8, "keen::uint32"), ("mode", 12, "keen::glimmer::Mode"),
                        ("flags", 13, "keen::Bitmask8<keen::glimmer::Mode>"), ("spare", 14, "keen::uint16"),
                        ("core", 16, "keen::LampCore")])
    t("keen::ecs::Lamp", 18, lamp[0], lamp[1], "keen::ecs::Component", lamp[2])
    t("keen::ecs::Torch", 18, 8, 4, "keen::ecs::Component", [("fuel", 0, "keen::float32"), ("hue", 4, "keen::glimmer::Hue")])
    types.sort(key=lambda e: e["qualifiedName"])
    return {"schema": 2, "side": "server", "exe_sha256": "0" * 62 + {"a": "0a", "b": "0b", "c": "0c"}[variant],
            "components": sorted(["keen::ecs::Component", "keen::ecs::Lamp", "keen::ecs::Torch"]), "events": [],
            "types": types}


for v in "abc":
    path = os.path.join(sys.argv[1], v + ".json")
    with open(path, "w", newline="\n") as f:
        json.dump(dump(v), f, indent=1)
        f.write("\n")
    print(f"mkstamp: {path}")
