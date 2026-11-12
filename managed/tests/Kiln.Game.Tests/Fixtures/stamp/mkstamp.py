#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes the three made-up schema-2 dumps of the layout-stamp tests (no game data: every name is invented).

    python3 managed/tests/Kiln.Game.Tests/Fixtures/stamp/mkstamp.py managed/tests/Kiln.Game.Tests/Fixtures/stamp

a.json  what the fixture mod (StampMod) is compiled against.
b.json  an unrelated field moved: Lamp.spare goes to offset 0, every other field of Lamp moves by 4; the fields the
        mod uses keep their names and types.
c.json  a field the mod uses changed type: Lamp.glow is a float64 instead of a float32.
d.json  the wrapper WickId gained a field (gen), so it is no longer a wrapper and has no implicit conversions; Lamp.wick
        keeps its type name and grows to 8 bytes.
e.json  the attribute component Glow's dataStorage holds sint32 instead of float32: its Values view is a Span<int>.
All of them also hold types the mod never names (Torch, Glimmer_Hue), which must not appear in its stamp.
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

    for qn, k, s in [("keen::uint8", 2, 1), ("keen::uint16", 4, 2), ("keen::uint32", 6, 4), ("keen::sint32", 7, 4),
                     ("keen::float32", 10, 4), ("keen::float64", 11, 8)]:
        t(qn, k, s, s)
    t("keen::ecs::Component", 18, 1, 1, fields=[])
    # a single-field wrapper (implicit conversions to and from uint), except in d
    if variant == "d":
        t("keen::ecs::WickId", 18, 8, 4, fields=[("id", 0, "keen::uint32"), ("gen", 4, "keen::uint16")])
    else:
        t("keen::ecs::WickId", 18, 4, 4, fields=[("id", 0, "keen::uint32")])
    # an attribute component (a Values view over dataStorage)
    t("keen::ecs::AttributeComponent", 18, 8, 4, "keen::ecs::Component",
      [("key", 0, "keen::uint32"), ("count", 4, "keen::uint16")])
    element = "keen::sint32" if variant == "e" else "keen::float32"
    t(f"keen::StaticArray<{element},2>", 19, 8, 4, element)
    t("keen::ecs::Glow", 18, 16, 4, "keen::ecs::AttributeComponent", [("dataStorage", 8, f"keen::StaticArray<{element},2>")])
    t("keen::glimmer::Mode", 12, 1, 1, "keen::uint8", values=[("Off", 0), ("Dim", 1), ("Bright", 2)])
    t("keen::glimmer::Hue", 12, 1, 1, "keen::uint8", values=[("Amber", 0), ("Blue", 1)])
    t("keen::Bitmask8<keen::glimmer::Mode>", 13, 1, 1, "keen::glimmer::Mode")
    t("keen::LampCore", 18, 8, 4, fields=[("heat", 0, "keen::float32"), ("ticks", 4, "keen::uint32")])
    if variant in ("a", "e"):
        lamp = (20, 4, [("glow", 0, "keen::float32"), ("wick", 4, "keen::ecs::WickId"), ("mode", 8, "keen::glimmer::Mode"),
                        ("flags", 9, "keen::Bitmask8<keen::glimmer::Mode>"), ("spare", 10, "keen::uint16"),
                        ("core", 12, "keen::LampCore")])
    elif variant == "b":
        lamp = (24, 4, [("spare", 0, "keen::uint16"), ("glow", 4, "keen::float32"), ("wick", 8, "keen::ecs::WickId"),
                        ("mode", 12, "keen::glimmer::Mode"), ("flags", 13, "keen::Bitmask8<keen::glimmer::Mode>"),
                        ("core", 16, "keen::LampCore")])
    elif variant == "d":
        lamp = (24, 4, [("glow", 0, "keen::float32"), ("wick", 4, "keen::ecs::WickId"), ("mode", 12, "keen::glimmer::Mode"),
                        ("flags", 13, "keen::Bitmask8<keen::glimmer::Mode>"), ("spare", 14, "keen::uint16"),
                        ("core", 16, "keen::LampCore")])
    else:
        lamp = (24, 8, [("glow", 0, "keen::float64"), ("wick", 8, "keen::ecs::WickId"), ("mode", 12, "keen::glimmer::Mode"),
                        ("flags", 13, "keen::Bitmask8<keen::glimmer::Mode>"), ("spare", 14, "keen::uint16"),
                        ("core", 16, "keen::LampCore")])
    t("keen::ecs::Lamp", 18, lamp[0], lamp[1], "keen::ecs::Component", lamp[2])
    t("keen::ecs::Torch", 18, 8, 4, "keen::ecs::Component", [("fuel", 0, "keen::float32"), ("hue", 4, "keen::glimmer::Hue")])
    types.sort(key=lambda e: e["qualifiedName"])
    return {"schema": 2, "side": "server", "exe_sha256": "0" * 62 + "0" + variant,
            "components": sorted(["keen::ecs::Component", "keen::ecs::Glow", "keen::ecs::Lamp", "keen::ecs::Torch"]),
            "events": [],
            "types": types}


for v in "abcde":
    path = os.path.join(sys.argv[1], v + ".json")
    with open(path, "w", newline="\n") as f:
        json.dump(dump(v), f, indent=1)
        f.write("\n")
    print(f"mkstamp: {path}")
