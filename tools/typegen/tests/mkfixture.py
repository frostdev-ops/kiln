#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes the made-up schema-2 fixture dump the generator tests use (no game data: every name is invented).

    python3 tools/typegen/tests/mkfixture.py managed/tests/Kiln.Game.Tests/Fixtures/gen/lanterns.json

It has every kind and every rule of the generator: primitives of each kind, a base class chain with fields
(an attribute component over keen::ecs::AttributeComponent), an empty base, a typedef chain, a typedef to a bitmask, bitmasks of 8, 16 and 64 bits, enums over
signed and unsigned integers (negative values too, one through a typedef), static arrays of primitives and of structs, single-field
wrappers, a field named after a C# keyword, a type of another namespace, an object reference, a guid, blob leaves,
another leaf kind, a static array of blob leaves, a tag component, components and events.
"""
import json
import sys


def fnv(s):
    h = 0x811C9DC5
    for b in s.encode():
        h = ((h ^ b) * 0x01000193) & 0xFFFFFFFF
    return f"0x{h:08X}"


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


for qn, k, s in [("keen::bool", 1, 1), ("keen::uint8", 2, 1), ("keen::sint8", 3, 1), ("keen::uint16", 4, 2),
                 ("keen::sint16", 5, 2), ("keen::uint32", 6, 4), ("keen::sint32", 7, 4), ("keen::uint64", 8, 8),
                 ("keen::sint64", 9, 8), ("keen::float32", 10, 4), ("keen::float64", 11, 8)]:
    t(qn, k, s, s)

# bases
t("keen::ecs::Component", 18, 1, 1, fields=[])
t("keen::ecs::Event", 18, 1, 1, fields=[])
t("keen::ecs::AttributeComponent", 18, 8, 4, "keen::ecs::Component",
  [("key", 0, "keen::HashKey32"), ("start", 4, "keen::uint16"), ("count", 6, "keen::uint8")])
t("keen::ecs::FloatMeter", 18, 8, 4, "keen::ecs::AttributeComponent", [])
t("keen::ecs::Warmth", 18, 24, 4, "keen::ecs::FloatMeter", [("dataStorage", 8, "keen::StaticArray<keen::float32,4>")])
# wrappers
t("keen::HashKey32", 18, 4, 4, fields=[("value", 0, "keen::uint32")])
t("keen::ecs::LampId", 18, 4, 4, fields=[("id", 0, "keen::uint32")])
t("keen::Moment", 18, 8, 8, fields=[("value", 0, "keen::sint64")])
# typedefs
t("keen::WickId", 17, 4, 4, "keen::ecs::LampId")
t("keen::Lumen", 17, 4, 4, "keen::float32")
t("keen::Brightness", 17, 4, 4, "keen::Lumen")
t("keen::TagSet", 17, 2, 2, "keen::Bitmask16<keen::LanternTag>")
# enums and bitmasks
t("keen::LanternMode", 12, 1, 1, "keen::uint8", values=[("Off", 0), ("Dim", 1), ("Bright", 2)])
t("keen::LanternTag", 12, 1, 1, "keen::uint8", values=[("Hanging", 0), ("Lit", 1), ("Smoky", 2), ("Hooded", 15)])
t("keen::Notch", 17, 2, 2, "keen::sint16")
t("keen::Tilt", 12, 2, 2, "keen::Notch", values=[("Left", -1), ("Level", 0), ("Right", 1)])
t("keen::Bitmask16<keen::LanternTag>", 14, 2, 2, "keen::LanternTag")
t("keen::Bitmask64<keen::LanternMode>", 16, 8, 8, "keen::LanternMode")
t("keen::glimmer::Kind", 12, 4, 4, "keen::uint32", values=[("Ember", 0), ("Flash", 1), ("event", 2)])
t("keen::Bitmask8<keen::glimmer::Kind>", 13, 1, 1, "keen::glimmer::Kind")
# structs
t("keen::ecs::Spark", 18, 8, 4, fields=[("heat", 0, "keen::float32"), ("age", 4, "keen::uint16"),
                                        ("tilt", 6, "keen::Tilt")])
t("keen::glimmer::Trail", 18, 12, 4, fields=[("length", 0, "keen::float32"), ("kind", 4, "keen::glimmer::Kind"),
                                             ("kinds", 8, "keen::Bitmask8<keen::glimmer::Kind>")])
t("keen::ecs::Comet", 18, 24, 8, "keen::ecs::Component",
  [("trail", 0, "keen::glimmer::Trail"), ("speed", 16, "keen::float64")])
t("keen::ecs::Gauges", 18, 56, 8, "keen::ecs::Component",
  [("on", 0, "keen::bool"), ("b", 1, "keen::uint8"), ("sb", 2, "keen::sint8"), ("us", 4, "keen::uint16"),
   ("s", 6, "keen::sint16"), ("ui", 8, "keen::uint32"), ("i", 12, "keen::sint32"), ("ul", 16, "keen::uint64"),
   ("l", 24, "keen::sint64"), ("f", 32, "keen::float32"), ("d", 40, "keen::float64"),
   ("modes", 48, "keen::Bitmask64<keen::LanternMode>")])
t("keen::ecs::Lantern", 18, 136, 8, "keen::ecs::Component",
  [("glow", 0, "keen::Brightness"), ("wick", 4, "keen::WickId"), ("mode", 8, "keen::LanternMode"),
   ("tags", 10, "keen::TagSet"), ("levels", 12, "keen::StaticArray<keen::sint16,4>"), ("lit", 24, "keen::Moment"),
   ("sparks", 32, "keen::StaticArray<keen::ecs::Spark,3>"), ("params", 56, "keen::uint32"),
   ("model", 64, "keen::ObjectReference<keen::LampModel>"), ("guid", 80, "keen::Guid"),
   ("notes", 96, "keen::BlobArray<keen::uint8>"), ("title", 104, "keen::BlobString"),
   ("opaque", 112, "keen::DsHandle"), ("labels", 120, "keen::StaticArray<keen::BlobString,2>")])
t("keen::ecs::Unlit", 18, 1, 1, "keen::ecs::Component", [])
t("keen::ecs::LanternLit", 18, 12, 4, "keen::ecs::Event",
  [("lantern", 0, "keen::ecs::LampId"), ("mode", 4, "keen::LanternMode"), ("wicks", 8, "keen::StaticArray<keen::uint8,4>")])
# arrays
t("keen::StaticArray<keen::float32,4>", 19, 16, 4, "keen::float32")
t("keen::StaticArray<keen::sint16,4>", 19, 8, 2, "keen::sint16")
t("keen::StaticArray<keen::uint8,4>", 19, 4, 1, "keen::uint8")
t("keen::StaticArray<keen::ecs::Spark,3>", 19, 24, 4, "keen::ecs::Spark")
t("keen::StaticArray<keen::BlobString,2>", 19, 16, 4, "keen::BlobString")
# leaves
t("keen::ObjectReference<keen::LampModel>", 28, 16, 4)
t("keen::Guid", 29, 16, 4)
t("keen::BlobArray<keen::uint8>", 24, 8, 4)
t("keen::BlobString", 25, 8, 4)
t("keen::DsHandle", 22, 8, 8)

types.sort(key=lambda e: e["qualifiedName"])
comps = sorted(["keen::ecs::Component", "keen::ecs::Comet", "keen::ecs::Gauges", "keen::ecs::Lantern",
                "keen::ecs::Unlit", "keen::ecs::Warmth"])
events = sorted(["keen::ecs::Event", "keen::ecs::LanternLit"])
dump = {"schema": 2, "side": "server", "exe_sha256": "0" * 63 + "1", "components": comps, "events": events,
        "types": types}
with open(sys.argv[1], "w", newline="\n") as f:
    json.dump(dump, f, indent=1)
    f.write("\n")
print(f"mkfixture: {len(types)} made-up types to {sys.argv[1]}")
