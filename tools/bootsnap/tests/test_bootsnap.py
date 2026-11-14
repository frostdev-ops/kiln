# SPDX-License-Identifier: MIT
"""tools/bootsnap tests on a synthetic archive and type graph the test builds (no game files).

    python3 -m unittest discover -s tools/bootsnap/tests -v

The expected JSON objects below are written by hand from the live reader's rules (native/services/content/
gamedata_walk.cpp node_json / leaf_json / leaf_of, depth 12), not produced by the code under test.
"""
import hashlib
import json
import os
import shutil
import signal
import struct
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOOL = HERE.parent
sys.path.insert(0, str(TOOL))
sys.path.insert(0, str(TOOL.parent / "kfc"))
sys.path.insert(0, str(TOOL.parent / "kfc" / "tests"))

from bootdata import kinds, live, snapshot  # noqa: E402
from kfc.util import guid_bytes  # noqa: E402
from test_kfc import build_kfc  # noqa: E402  (tools/kfc's own synthetic KFC4 writer, read-only reuse)

import bootsnap as cli  # noqa: E402

fnv = kinds.fnv1a32

# ---------------------------------------------------------------- the synthetic type graph
K = live
U8, U16, U32, U64, S8, S16, F32, BOOL = "keen::uint8", "keen::uint16", "keen::uint32", "keen::uint64", "keen::sint8", "keen::sint16", "keen::float32", "keen::bool"


def T(name, kind, size, inner=None, count=0, fields=None, values=None):
    d = {"name": name, "hash": fnv(name), "layout": 0, "size": size, "align": 4, "kind": kind, "flags": 0, "inner": inner,
         "count": len(fields) if fields is not None else (len(values) if values is not None else count), "rva": 0}
    if fields is not None:
        d["fields"] = [{"name": n, "type": t, "offset": o} for n, t, o in fields]
    if values is not None:
        d["values"] = [{"name": n, "value": v} for n, v in values]
    return d


TYPES = [
    T(U8, K.K_U8, 1), T(U16, K.K_U16, 2), T(U32, K.K_U32, 4), T(U64, K.K_U64, 8), T(S8, K.K_S8, 1), T(S16, K.K_S16, 2),
    T(F32, K.K_F32, 4), T(BOOL, K.K_BOOL, 1), T("keen::int64", K.K_S64, 8),
    T("keen::Guid", K.K_GUID, 16), T("keen::ObjectReference<keen::ItemInfo>", K.K_REF, 16),
    T("keen::ItemRef", K.K_TYPEDEF, 16, inner="keen::ObjectReference<keen::ItemInfo>"),
    T("keen::Time", K.K_STRUCT, 8, fields=[("value", "keen::int64", 0)]),
    T("keen::HashKey32", K.K_STRUCT, 4, fields=[("value", U32, 0)]),
    T("keen::BlobString", K.K_BLOBSTRING, 8),
    T("keen::Rarity", K.K_ENUM, 1, inner=U8, values=[("Common", 0), ("Rare", 1), ("Epic", 2)]),
    T("keen::Sign", K.K_ENUM, 1, inner=S8, values=[("Minus", 0xFFFFFFFFFFFFFFFF), ("Zero", 0)]),
    T("keen::ItemFlag", K.K_ENUM, 1, inner=U8, values=[("A", 0), ("B", 3)]),
    T("keen::ItemFlags", K.K_BM8, 1, inner="keen::ItemFlag"),
    T("keen::BlobArray<keen::uint32>", K.K_BLOBARRAY, 8, inner=U32),
    T("keen::BlobArray<keen::uint8>", K.K_BLOBARRAY, 8, inner=U8),
    T("keen::StaticArray<keen::float32,3>", K.K_ARRAY, 12, inner=F32, count=3),
    T("keen::BlobOptional<keen::uint32>", K.K_BLOBOPT, 4, inner=U32),
    T("keen::Deep", K.K_STRUCT, 8, fields=[("next", "keen::BlobOptional<keen::Deep>", 0), ("level", U32, 4)]),
    T("keen::BlobOptional<keen::Deep>", K.K_BLOBOPT, 4, inner="keen::Deep"),
    T("keen::impact::ImpactConfig", K.K_STRUCT, 4, fields=[("configId", U32, 0)]),
    T("keen::impact::FloatConfig", K.K_STRUCT, 8, inner="keen::impact::ImpactConfig", fields=[("value", F32, 4)]),
    T("keen::impact::ScaledConfig", K.K_STRUCT, 12, inner="keen::impact::ImpactConfig",
      fields=[("value", F32, 4), ("scaleFactor", F32, 8)]),
    T("keen::BlobVariant<keen::impact::ImpactConfig>", K.K_BLOBVAR, 12, inner="keen::impact::ImpactConfig"),
    T("keen::BlobArray<keen::BlobVariant<keen::impact::ImpactConfig>>", K.K_BLOBARRAY, 8,
      inner="keen::BlobVariant<keen::impact::ImpactConfig>"),
    T("keen::impact::Configurations", K.K_STRUCT, 16, fields=[
        ("simple", "keen::BlobArray<keen::BlobVariant<keen::impact::ImpactConfig>>", 0),
        ("scaled", "keen::BlobArray<keen::BlobVariant<keen::impact::ImpactConfig>>", 8)]),
    T("keen::VoxelData", K.K_STRUCT, 20, fields=[("placeVoxelMaterial", "keen::Guid", 0), ("placeVoxelMaterialId", U8, 16)]),
    T("keen::ItemEquipment", K.K_STRUCT, 20, fields=[("voxelData", "keen::VoxelData", 0)]),
    T("keen::ItemInfo", K.K_STRUCT, 180, fields=[
        ("debugName", "keen::BlobString", 0), ("objectId", "keen::Guid", 8), ("rarity", "keen::Rarity", 24),
        ("flags", "keen::ItemFlags", 25), ("sign", "keen::Sign", 26), ("maxStackSize", U32, 28), ("weight", F32, 32),
        ("broken", F32, 36), ("created", "keen::Time", 40), ("id", "keen::HashKey32", 48),
        ("tags", "keen::BlobArray<keen::uint32>", 52), ("icon", "keen::BlobArray<keen::uint8>", 60),
        ("position", "keen::StaticArray<keen::float32,3>", 68), ("extra", "keen::BlobOptional<keen::uint32>", 80),
        ("none", "keen::BlobOptional<keen::uint32>", 84), ("ref", "keen::ItemRef", 88),
        ("nullRef", "keen::ObjectReference<keen::ItemInfo>", 104), ("badName", "keen::BlobString", 120),
        ("impactValues", "keen::impact::Configurations", 128), ("equipment", "keen::ItemEquipment", 144),
        ("deep", "keen::BlobOptional<keen::Deep>", 164), ("big", U64, 168), ("neg", S16, 176),
        ("", U8, 178), ("unknownType", "keen::NotInTheGraph", 179)]),
    T("keen::ecs::Component", K.K_STRUCT, 1, fields=[("enabled", BOOL, 0)]),
    T("keen::ecs::Health", K.K_STRUCT, 8, inner="keen::ecs::Component", fields=[("points", F32, 4)]),
    T("keen::BlobVariant<keen::ecs::Component>", K.K_BLOBVAR, 12, inner="keen::ecs::Component"),
    T("keen::BlobArray<keen::BlobVariant<keen::ecs::Component>>", K.K_BLOBARRAY, 8, inner="keen::BlobVariant<keen::ecs::Component>"),
    T("keen::ecs::TemplateResource", K.K_STRUCT, 16, fields=[
        ("name", "keen::BlobString", 0), ("components", "keen::BlobArray<keen::BlobVariant<keen::ecs::Component>>", 8)]),
    T("keen::SkillTreeNode", K.K_STRUCT, 8, fields=[("id", U32, 0), ("cost", U32, 4)]),
    T("keen::BlobArray<keen::SkillTreeNode>", K.K_BLOBARRAY, 8, inner="keen::SkillTreeNode"),
    T("keen::SkillTreeResource", K.K_STRUCT, 8, fields=[("nodes", "keen::BlobArray<keen::SkillTreeNode>", 0)]),
    T("keen::BuildingConfig", K.K_STRUCT, 8, fields=[("buildingMaterial", U8, 0), ("healthPoints", F32, 4)]),
    T("keen::TerrainConfig", K.K_STRUCT, 8, fields=[("terrainMaterial", U8, 0), ("healthPoints", F32, 4)]),
    T("keen::BlobArray<keen::BuildingConfig>", K.K_BLOBARRAY, 8, inner="keen::BuildingConfig"),
    T("keen::BlobArray<keen::TerrainConfig>", K.K_BLOBARRAY, 8, inner="keen::TerrainConfig"),
    T("keen::TerraformingEfficiencyRegistryResource", K.K_STRUCT, 16, fields=[
        ("buildingConfigs", "keen::BlobArray<keen::BuildingConfig>", 0), ("terrainConfigs", "keen::BlobArray<keen::TerrainConfig>", 8)]),
    T("keen::ImpactVariable", K.K_STRUCT, 16, fields=[("configId", U32, 0), ("dbgName", "keen::BlobString", 4), ("type", U32, 12)]),
    T("keen::BlobArray<keen::ImpactVariable>", K.K_BLOBARRAY, 8, inner="keen::ImpactVariable"),
    T("keen::ImpactProgram", K.K_STRUCT, 8, fields=[("dataLayout", "keen::BlobArray<keen::ImpactVariable>", 0)]),
]
DUMP = {"exe_sha256": "0" * 64, "image_base": 0x140000000, "count": len(TYPES), "types": TYPES}


# ---------------------------------------------------------------- blobs, by hand
class Blob:
    """A resource under construction: the struct, then out-of-line data, every offset relative to its own field."""

    def __init__(self, size):
        self.b = bytearray(size)

    def put(self, fmt, at, *v):
        struct.pack_into(fmt, self.b, at, *v)

    def tail(self, data):
        while len(self.b) % 4:
            self.b.append(0)
        at = len(self.b)
        self.b += data
        return at

    def string(self, at, s):
        off = self.tail(s)
        self.put("<iI", at, off - at, len(s))

    def array(self, at, data, n):
        off = self.tail(data)
        self.put("<iI", at, off - at, n)
        return off

    def optional(self, at, data):
        self.put("<i", at, self.tail(data) - at)

    def variant(self, at, type_name, data):
        off = self.tail(data)
        self.put("<IiI", at, fnv(type_name) if type_name else 0, off - (at + 4), len(data))

    def bytes(self):
        return bytes(self.b)


G1 = "11111111-2222-3333-4444-555555555555"
G2 = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
GVOX = "01234567-89ab-cdef-0123-456789abcdef"


def item_blob(name, full=False, voxel=None):
    b = Blob(180)
    if name is not None:
        b.string(0, name.encode())
    if voxel is not None:
        b.b[144:160] = guid_bytes(GVOX)
        b.b[160] = voxel
    if not full:
        return b.bytes()
    b.b[8:24] = guid_bytes(G1)
    b.b[24] = 2                     # rarity Epic
    b.b[25] = 0b01001001            # flags A (0), B (3) and bit 6, which names nothing
    b.b[26] = 0xFF                  # sign: -1 = Minus
    b.put("<I", 28, 250)
    b.put("<f", 32, 1.5)
    b.put("<f", 36, float("nan"))
    b.put("<q", 40, 1_500_000_000)  # keen::Time in nanoseconds
    b.put("<I", 48, 0xDEADBEEF)
    b.array(52, struct.pack("<3I", 1, 2, 3), 3)
    b.array(60, bytes(range(20)), 20)
    b.put("<3f", 68, 1.0, 2.0, 0.25)
    b.optional(80, struct.pack("<I", 9))
    b.b[88:104] = guid_bytes(G2)
    b.string(120, "Caf\u00e9".encode("utf-8"))  # not printable ASCII: the live reader shows ""
    # impact values: three simple configs, one scaled
    simple = b.array(128, bytes(12 * 3), 3)
    for i, (cid, v) in enumerate(((0xaf236ece, 2.5), (0x11111111, 4.0), (0x22222222, 7.0))):
        b.variant(simple + 12 * i, "keen::impact::FloatConfig", struct.pack("<If", cid, v))
    scaled = b.array(136, bytes(12), 1)
    b.variant(scaled, "keen::impact::ScaledConfig", struct.pack("<Iff", 0x33333333, 1.0, 0.5))
    # a chain of 14 keen::Deep
    at = 164
    for level in range(14):
        node = struct.pack("<iI", 0, level)
        off = b.tail(node)
        b.put("<i", at, off - at)
        at = off
    b.put("<Q", 168, 0xFFFFFFFFFFFFFFFF)
    b.put("<h", 176, -5)
    return b.bytes()


def deep(level):
    if level == 10:
        return {"next": "...", "level": 10}
    return {"next": deep(level + 1), "level": level}


# The live reader's object for item_blob("Material_T1_Wood_Logs", full=True), by hand.
EXPECTED_ITEM = {
    "debugName": "Material_T1_Wood_Logs",
    "objectId": G1,
    "rarity": "Epic",
    "flags": ["A", "B", 64],
    "sign": "Minus",
    "maxStackSize": 250,
    "weight": 1.5,
    "broken": None,                 # NaN: nlohmann writes null
    "created": 1.5,                 # keen::Time: seconds
    "id": 0xDEADBEEF,               # a one-member struct shows its scalar
    "tags": [1, 2, 3],
    "icon": "(20 bytes)",           # more than 16 bytes
    "position": [1.0, 2.0, 0.25],
    "extra": None,                  # an optional scalar: node_json prints a leaf only before deref, so live it is null
    "none": None,
    "ref": G2,
    "nullRef": "none",
    "badName": "",
    "impactValues": {
        "ManaCost": {"$config": "FloatConfig", "value": 2.5},       # read by C++: always named
        "Radius": {"$config": "FloatConfig", "value": 4.0},         # named by an impact program
        "0x22222222": {"$config": "FloatConfig", "value": 7.0},     # no name
        "0x33333333": {"$config": "ScaledConfig", "value": {"value": 1.0, "scaleFactor": 0.5}},  # its name is two ids'
    },
    "equipment": {"voxelData": {"placeVoxelMaterial": "none", "placeVoxelMaterialId": 0}},
    "deep": deep(0),
    "big": 0xFFFFFFFFFFFFFFFF,
    "neg": -5,
    "unknownType": None,
}


def program_blob(variables):
    b = Blob(8)
    data = b.array(0, bytes(16 * len(variables)), len(variables))
    for i, (cid, name) in enumerate(variables):
        b.put("<I", data + 16 * i, cid)
        b.string(data + 16 * i + 4, name.encode())
        b.put("<I", data + 16 * i + 12, 1)
    return b.bytes()


PROGRAMS = [
    [(0x11111111, "Radius::value"), (0x33333333, "Dup"), (0x44444444, "Dup"), (0x55555555, "impact_nodes.Value")],
    [(0x11111111, "Radius"), (0x66666666, "Beta"), (0x66666666, "Alpha")],
]


def template_blob(name, components):
    b = Blob(16)
    if name:
        b.string(0, name.encode())
    data = b.array(8, bytes(12 * len(components)), len(components))
    for i, (tn, payload) in enumerate(components):
        b.variant(data + 12 * i, tn, payload)
    return b.bytes()


def skills_blob(nodes):
    b = Blob(8)
    b.array(0, b"".join(struct.pack("<II", i, c) for i, c in nodes), len(nodes))
    return b.bytes()


def terraforming_blob(building, terrain):
    b = Blob(16)
    b.array(0, b"".join(struct.pack("<B3xf", m, h) for m, h in building), len(building))
    b.array(8, b"".join(struct.pack("<B3xf", m, h) for m, h in terrain), len(terrain))
    return b.bytes()


ITEM_T, TPL_T = fnv("keen::ItemInfo"), fnv("keen::ecs::TemplateResource")


def resources():
    """(guid, type hash, part, bytes) in the archive's order."""
    g = lambda i: f"{i:08x}-0000-4000-8000-000000000000"  # noqa: E731
    return [
        (g(1), ITEM_T, 0, item_blob("Material_T1_Wood_Logs", full=True)),
        (g(2), ITEM_T, 0, item_blob("Block_T2_Stone_Rough", voxel=12 + 128)),
        (g(3), ITEM_T, 0, item_blob("_Block_Stone_Rough_Crafting", voxel=13 + 128)),
        (g(4), ITEM_T, 0, item_blob("Build_TerrainMaterial_T1_Sand", voxel=5)),
        (g(5), ITEM_T, 0, item_blob("Material_T1_Wood_Logs")),  # the same name again: skipped
        (g(6), ITEM_T, 0, item_blob(None)),                     # no name: skipped
        (g(7), TPL_T, 0, template_blob("NPC_Workshop_Fisher_Trader", [
            ("keen::ecs::Health", struct.pack("<?3xf", True, 50.0)), ("keen::NoSuchType", b"\0" * 8), ("", b"")])),
        (g(8), TPL_T, 0, template_blob("", [])),                # no name: named by its GUID
        (g(9), fnv("keen::SkillTreeResource"), 0, skills_blob([(0x8efcc472, 3), (0x8efcc472, 4), (1, 1)])),
        (g(10), fnv("keen::TerraformingEfficiencyRegistryResource"), 0,
         terraforming_blob([(12, 100.0), (13, 120.0), (22, 80.0)], [(5, 10.0), (6, 12.0)])),
        (g(11), fnv("keen::ImpactProgram"), 0, program_blob(PROGRAMS[0])),
        (g(12), fnv("keen::ImpactProgram"), 0, program_blob(PROGRAMS[1])),
    ]


def make_game(d, res=None, exe=b"MZ synthetic server exe", name="enshrouded_server"):
    """A game folder: <name>.exe (any bytes: only hashed), <name>.kfc / .kfc_resources / _NNN.dat."""
    d = Path(d)
    d.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(dir=d))
    build_kfc(work, res or resources(), [b"content"])
    for f in work.iterdir():
        f.rename(d / f.name.replace("test", name, 1))
    work.rmdir()
    (d / f"{name}.exe").write_bytes(exe)
    return d


def set_version(kfc_path, new):
    """Rewrites the version string in place (same length)."""
    b = bytearray(Path(kfc_path).read_bytes())
    rel, n = struct.unpack_from("<II", b, 0x10)
    assert len(new) == n
    b[0x10 + rel:0x10 + rel + n] = new
    Path(kfc_path).write_bytes(bytes(b))


def source_for(exe):
    """The CLI's own open_source, with the synthetic type graph in place of the executable's reflection."""
    return cli.open_source(exe, extract=lambda _exe: DUMP)


# ================================================================ S1: the decoder core
class LiveJsonTests(unittest.TestCase):
    def setUp(self):
        self.g = live.Graph(DUMP)
        self.c = live.ConfigNames()
        for p in PROGRAMS:
            self.c.add_program(self.g, program_blob(p))
        self.c.finish()
        self.w = live.Walker(self.g, self.c)

    def test_item_is_the_live_readers_object(self):
        v = self.w.json("keen::ItemInfo", item_blob("Material_T1_Wood_Logs", full=True))
        self.assertEqual(v, EXPECTED_ITEM)
        self.assertEqual(list(v), list(EXPECTED_ITEM), "members in the reflection's order")

    def test_json_text_parses_back_equal(self):
        v = self.w.json("keen::ItemInfo", item_blob("Material_T1_Wood_Logs", full=True))
        raw = json.dumps(v, separators=(",", ":"), allow_nan=False)
        self.assertEqual(json.loads(raw), EXPECTED_ITEM)

    def test_config_vocabulary(self):
        c = self.c
        self.assertEqual(c.name(0xaf236ece), "ManaCost")
        self.assertEqual(c.name(0x11111111), "Radius", "the name up to '::', used twice")
        self.assertEqual(c.name(0x33333333), "0x33333333", "a name of two ids names neither")
        self.assertEqual(c.name(0x55555555), "0x55555555", "impact_nodes. names say nothing")
        self.assertEqual(c.name(0x66666666), "Alpha", "a tie: the first in byte order")
        self.assertEqual(c.name(0x77777777), "0x77777777")

    def test_template_components_variants(self):
        blob = template_blob("NPC_Workshop_Fisher_Trader", [
            ("keen::ecs::Health", struct.pack("<?3xf", True, 50.0)), ("keen::NoSuchType", b"\0" * 8), ("", b"")])
        self.assertEqual(self.w.json("keen::ecs::TemplateResource", blob), {
            "name": "NPC_Workshop_Fisher_Trader",
            "components": [{"$type": "Health", "enabled": True, "points": 50.0}, None, None]})

    def test_leaf_rules(self):
        g = self.g
        leaf = lambda t, raw: self.w.leaf_json(g.leaf(g.named(t)), raw)  # noqa: E731
        self.assertEqual(leaf("keen::Rarity", b"\x07"), 7, "an enum value without a name is the number")
        self.assertEqual(leaf("keen::Sign", b"\x00"), "Zero")
        self.assertEqual(leaf("keen::ItemFlags", b"\x00"), [])
        self.assertEqual(leaf(U64, struct.pack("<Q", 5)), 5)
        self.assertEqual(leaf(S16, struct.pack("<h", -2)), -2)
        self.assertEqual(leaf(F32, struct.pack("<f", float("inf"))), None)
        self.assertEqual(leaf("keen::Guid", bytes(16)), "none")
        self.assertIsNone(g.leaf(g.named("keen::Deep")), "a struct of two members is no leaf")
        self.assertEqual(g.leaf(g.named("keen::ItemRef"))[0], "guid", "a typedef of a reference")

    def test_strings_and_reads_outside_the_blob(self):
        b = live.Blob(b"\x08\x00\x00\x00\x03\x00\x00\x00abc\x01")
        self.assertEqual(b.blob_string(0), "abc")
        self.assertEqual(live.Blob(b"\x08\x00\x00\x00\x04\x00\x00\x00abc\x01").blob_string(0), "", "a control byte")
        self.assertEqual(live.Blob(b"\x08\x00\x00\x00\x09\x00\x00\x00abc").blob_string(0), "", "past the end")
        self.assertEqual(live.Blob(struct.pack("<iI", 8, 513) + b"a" * 513).blob_string(0), "", "over 512 bytes")
        self.assertEqual(live.Blob(struct.pack("<iI", 0, 3)).blob_string(0), "", "rel 0")
        # a BlobArray whose elements lie past the blob: each read fails, each element is null
        blob = Blob(180)
        blob.put("<iI", 52, 1000, 2)
        v = self.w.json("keen::ItemInfo", blob.bytes())
        self.assertEqual(v["tags"], [None, None])
        self.assertEqual(v["debugName"], "")
        blob.put("<iI", 52, 0, 5)  # rel 0: empty whatever the count
        self.assertEqual(self.w.json("keen::ItemInfo", blob.bytes())["tags"], [])
        blob.put("<iI", 52, 8, 1000001)  # more than a million: not an array
        self.assertIsNone(self.w.json("keen::ItemInfo", blob.bytes())["tags"])
        self.assertIsNone(self.w.json("keen::ItemInfo", b"\0" * 10)["maxStackSize"], "a struct cut short: null")

    def test_small_byte_arrays_are_lists(self):
        blob = Blob(180)
        blob.array(60, bytes([7] * 16), 16)
        self.assertEqual(self.w.json("keen::ItemInfo", blob.bytes())["icon"], [7] * 16)

    def test_brief(self):
        self.assertEqual(live.brief_of("keen::ecs::LifeTime"), "LifeTime")
        self.assertEqual(live.brief_of("keen::BlobArray<keen::X>"), "BlobArray<keen::X>")
        self.assertEqual(live.brief_of("Plain"), "Plain")


class KindTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.game = make_game(Path(self.tmp.name) / "game")
        self.src = source_for(self.game / "enshrouded_server.exe")

    def tearDown(self):
        self.src.close()
        self.tmp.cleanup()

    def names(self, kind):
        return [r.name for r in self.src.rows(kind)]

    def test_names(self):
        self.assertEqual(self.names("items"), ["Material_T1_Wood_Logs", "Block_T2_Stone_Rough", "_Block_Stone_Rough_Crafting",
                                               "Build_TerrainMaterial_T1_Sand", "Material_T1_Wood_Logs", ""])
        self.assertEqual(self.names("templates"), ["NPC_Workshop_Fisher_Trader", "00000008-0000-4000-8000-000000000000"])
        self.assertEqual(self.names("skills"), ["0x8efcc472", "0x8efcc472", "0x00000001"])
        self.assertEqual(self.names("materials"), ["StoneRough", "Cell141", "Cell150", "TerrainSand", "Cell6"])

    def test_row_values_and_backing(self):
        rows = {r.name: r for r in self.src.rows("materials")}
        self.assertEqual(rows["StoneRough"].value(), {"buildingMaterial": 12, "healthPoints": 100.0})
        self.assertEqual(rows["Cell6"].value(), {"terrainMaterial": 6, "healthPoints": 12.0})
        self.assertEqual(rows["StoneRough"].key.guid, "0000000a-0000-4000-8000-000000000000")
        skills = list(self.src.rows("skills"))
        self.assertEqual(skills[0].value(), {"id": 0x8efcc472, "cost": 3})
        self.assertEqual(skills[2].value(), {"id": 1, "cost": 1})
        self.assertEqual(next(self.src.rows("items")).value(), EXPECTED_ITEM)

    def test_material_name_rule(self):
        m = kinds.material_name
        self.assertEqual(m("Block_T2_Stone_Rough"), "StoneRough")
        self.assertEqual(m("__Block_Wood_Crafting_Reward_T10"), "Wood")
        self.assertEqual(m("Build_TerrainMaterial_Sand"), "TerrainSand")
        self.assertEqual(m("Block_stone_t2x"), "StoneT2x")
        self.assertIsNone(m("Material_T1_Wood_Logs"))


# ================================================================ S2: the writer and the key
class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.game = make_game(Path(self.tmp.name) / "game")
        self.exe = self.game / "enshrouded_server.exe"
        self.root = self.game / "kiln" / "data" / "boot-gamedata"

    def tearDown(self):
        self.tmp.cleanup()

    def make(self, **kw):
        return snapshot.make(self.game, source_for, **kw)

    def key(self):
        return snapshot.snapshot_key(hashlib.sha256(self.exe.read_bytes()).hexdigest(), snapshot.kfc_version(self.game / "enshrouded_server.kfc"))

    def test_key_is_the_contracts(self):
        # exe SHA-256 as 64 lower-case hex, "\n", the version string; the first 32 hex digits of SHA-256 over that
        exe = "9fd4f8cb69e9633a470922691c90d9c54174a82cde9479d9d580ef8df579bf10"
        self.assertEqual(hashlib.sha256(b"MZ fixed exe for the key vector").hexdigest(), exe)
        version = b"1287369|^/game38/branches/release_1.0|2026-11-04T11:17:54.051725Z"
        self.assertEqual(snapshot.snapshot_key(exe, version), "f6f534acc0b48c8e981864a6322c43c5")
        self.assertEqual(snapshot.snapshot_key(exe.upper(), version.decode()), "f6f534acc0b48c8e981864a6322c43c5")
        self.assertEqual(snapshot.kfc_version(self.game / "enshrouded_server.kfc"), b"1|^/test|2026-11-04")

    def test_layout_and_index(self):
        r = self.make()
        self.assertEqual(r["status"], "made")
        key = self.key()
        d = self.root / key
        self.assertEqual(Path(r["path"]), d)
        self.assertEqual(sorted(p.name for p in d.iterdir()), sorted(
            ["index.json"] + [f"{k}.index.json" for k in kinds.KINDS] + [f"{k}.data" for k in kinds.KINDS]))
        raw = (d / "index.json").read_bytes()
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
        idx = json.loads(raw)
        self.assertEqual(idx["format"], 1)
        self.assertEqual(idx["key"], key)
        self.assertEqual(idx["side"], "server")
        self.assertEqual(idx["exeSha256"], hashlib.sha256(self.exe.read_bytes()).hexdigest())
        self.assertEqual(idx["kfcVersion"], "1|^/test|2026-11-04")
        self.assertEqual(idx["gameBuild"], "1")
        self.assertTrue(idx["tool"].startswith("kiln-bootsnap"))
        self.assertRegex(idx["written"], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")
        self.assertEqual(idx["counts"], {"items": 4, "templates": 2, "skills": 2, "materials": 5})
        self.assertIn({"kind": "items", "name": "Material_T1_Wood_Logs", "reason": "duplicate name"}, idx["skipped"])
        self.assertIn({"kind": "skills", "name": "0x8efcc472", "reason": "duplicate name"}, idx["skipped"])
        self.assertTrue(any(s["kind"] == "items" and s["reason"].startswith("no name") for s in idx["skipped"]))
        self.assertEqual(idx["runtimeOnly"], [])
        items = json.loads((d / "items.index.json").read_text())
        self.assertEqual(items["format"], 1)
        self.assertEqual(items["kind"], "items")
        e = items["entries"]["Material_T1_Wood_Logs"]
        self.assertEqual(e[2:], ["00000001-0000-4000-8000-000000000000", f"{ITEM_T:08x}", 0], "the first of the two is stored")
        data = (d / "items.data").read_bytes()
        self.assertEqual(json.loads(data[e[0]:e[0] + e[1]]), EXPECTED_ITEM)
        total = 0
        for k in kinds.KINDS:  # every entry is one complete object; the values lie back to back
            ix = json.loads((d / f"{k}.index.json").read_text())
            blob = (d / f"{k}.data").read_bytes()
            spans = sorted((v[0], v[1]) for v in ix["entries"].values())
            at = 0
            for off, n in spans:
                self.assertEqual(off, at)
                self.assertIsInstance(json.loads(blob[off:off + n]), dict)
                at += n
            self.assertEqual(at, len(blob))
            total += len(ix["entries"])
        self.assertEqual(total, sum(idx["counts"].values()))
        got = snapshot.read_value(d, "skills", "0x8efcc472")
        self.assertEqual(got[1], {"id": 0x8efcc472, "cost": 3})
        self.assertEqual(got[0][3], f"{fnv('keen::SkillTreeResource'):08x}")

    def test_same_key_writes_nothing(self):
        self.make()
        d = self.root / self.key()
        stamp = {p.name: (p.stat().st_mtime_ns, p.stat().st_ino) for p in d.iterdir()}
        (d / "marker").write_text("kept")
        t = time.time()
        r = self.make()
        self.assertEqual(r["status"], "reused")
        self.assertLess(time.time() - t, 1.0)
        self.assertEqual((d / "marker").read_text(), "kept", "the same directory, untouched")
        for p in d.iterdir():
            if p.name != "marker":
                self.assertEqual((p.stat().st_mtime_ns, p.stat().st_ino), stamp[p.name])

    def test_another_key_replaces(self):
        self.make()
        old = self.key()
        set_version(self.game / "enshrouded_server.kfc", b"2|^/test|2026-11-05")
        new = self.key()
        self.assertNotEqual(old, new)
        r = self.make()
        self.assertEqual((r["status"], r["key"]), ("made", new))
        self.assertTrue(snapshot.valid(self.root / new, new))
        self.assertFalse((self.root / old).exists(), "the snapshot of the other key is removed")
        self.exe.write_bytes(b"MZ another exe")  # a new exe is a new key too
        self.assertEqual(self.make()["status"], "made")
        self.assertEqual([p.name for p in self.root.iterdir()], [self.key()])

    def test_force_rebuilds(self):
        self.make()
        d = self.root / self.key()
        (d / "marker").write_text("x")
        self.assertEqual(self.make(force=True)["status"], "made")
        self.assertFalse((d / "marker").exists())
        self.assertTrue(snapshot.valid(d, self.key()))

    def test_a_broken_snapshot_is_replaced(self):
        self.make()
        d = self.root / self.key()
        (d / "index.json").write_text("{\"format\": 1, \"key\": \"x\"}")
        self.assertFalse(snapshot.valid(d, self.key()))
        self.assertEqual(self.make()["status"], "made")
        (d / "materials.data").unlink()
        self.assertFalse(snapshot.valid(d, self.key()), "all eight kind files")

    def test_a_failed_run_leaves_no_snapshot(self):
        def boom(kind, tmp):
            if kind == "templates":
                raise RuntimeError("stop")
        with self.assertRaises(RuntimeError):
            self.make(after_kind=boom)
        self.assertFalse((self.root / self.key()).exists())
        self.assertEqual(list(self.root.iterdir()), [], "its .tmp directory is gone too")

    def test_a_killed_run_leaves_no_readable_snapshot(self):
        mark = Path(self.tmp.name) / "items-written"
        p = subprocess.Popen([sys.executable, "-I", str(Path(__file__)), "--driver", str(self.game), str(mark)])
        try:
            for _ in range(600):
                if mark.exists() or p.poll() is not None:
                    break
                time.sleep(0.05)
            self.assertTrue(mark.exists(), "the driver wrote its first kind")
            os.kill(p.pid, signal.SIGKILL)
            p.wait()
        finally:
            if p.poll() is None:
                p.kill()
        key = self.key()
        self.assertFalse((self.root / key).exists(), "no directory of the key")
        left = list(self.root.iterdir())
        self.assertEqual(len(left), 1)
        self.assertTrue(left[0].name.startswith(key + ".tmp-"))
        self.assertTrue((left[0] / "items.data").exists() and not (left[0] / "index.json").exists())
        self.assertFalse(snapshot.valid(left[0], key))
        self.assertEqual(self.make()["status"], "made")
        self.assertEqual([x.name for x in self.root.iterdir()], [key], "the next run removes what the killed one left")

    def test_reads_the_archive_another_loader_kept_aside(self):
        g = self.game
        for n in ("enshrouded_server.kfc", "enshrouded_server.kfc_resources"):
            (g / n).rename(g / (n + ".bak"))
        res = resources()
        res[0] = (res[0][0], res[0][1], 0, item_blob("Material_T1_Wood_Logs"))  # a loader's patched item
        work = Path(tempfile.mkdtemp(dir=self.tmp.name))
        build_kfc(work, res, [b"content"])
        for n in ("kfc", "kfc_resources"):
            (work / f"test.{n}").rename(g / f"enshrouded_server.{n}")
        r = self.make()
        d = Path(r["path"])
        self.assertEqual(r["archive"], "backup")
        self.assertEqual(snapshot.read_value(d, "items", "Material_T1_Wood_Logs")[1], EXPECTED_ITEM, "the game's own item")
        # an older EML kept the header alone: the backup header with the live resources (EML reads it so too)
        patched = (g / "enshrouded_server.kfc_resources").read_bytes()
        (g / "enshrouded_server.kfc_resources").unlink()
        (g / "enshrouded_server.kfc_resources.bak").rename(g / "enshrouded_server.kfc_resources")
        r = self.make(force=True)
        self.assertEqual(r["archive"], "backup header")
        self.assertEqual(snapshot.read_value(Path(r["path"]), "items", "Material_T1_Wood_Logs")[1], EXPECTED_ITEM)
        # after a game update the backup is of the old build and the live pair is the game's new one: the live pair is read
        (g / "enshrouded_server.kfc_resources").write_bytes(patched)
        set_version(g / "enshrouded_server.kfc.bak", b"0|^/test|2026-11-01")
        r = self.make(force=True)
        self.assertEqual(r["archive"], "plain (the backup is of another game build)")
        self.assertNotEqual(snapshot.read_value(Path(r["path"]), "items", "Material_T1_Wood_Logs")[1], EXPECTED_ITEM, "the live item")

    def test_values_over_the_bound_are_skipped(self):
        self.assertEqual(snapshot.VALUE_MAX, 4 * 1024 * 1024 - 256)
        old = snapshot.VALUE_MAX
        snapshot.VALUE_MAX = 60  # every value but the smallest is over it
        try:
            r = self.make()
        finally:
            snapshot.VALUE_MAX = old
        idx = json.loads((Path(r["path"]) / "index.json").read_text())
        self.assertIn({"kind": "items", "name": "Material_T1_Wood_Logs", "reason": "over 4 MiB"}, idx["skipped"])
        items = json.loads((Path(r["path"]) / "items.index.json").read_text())
        self.assertNotIn("Material_T1_Wood_Logs", items["entries"])
        self.assertEqual(idx["counts"]["skills"], 2, "small values are kept")

    def test_a_kind_that_cannot_be_read_is_named(self):
        res = [r for r in resources() if r[1] != fnv("keen::SkillTreeResource")]
        shutil.rmtree(self.game)
        self.game = make_game(Path(self.tmp.name) / "game", res)
        r = self.make()
        self.assertEqual((r["status"], r["emptyKinds"]), ("made", ["skills"]))
        idx = json.loads((Path(r["path"]) / "index.json").read_text())
        self.assertEqual(idx["counts"]["skills"], 0)
        self.assertIn({"kind": "skills", "name": "", "reason": "kind not read: the archive has no keen::SkillTreeResource"}, idx["skipped"])
        self.assertEqual(self.make()["emptyKinds"], ["skills"], "a reuse says it too")

    def test_the_archive_is_closed_before_the_rename(self):
        seen, real = [], os.rename
        made = []

        def factory(exe):
            src = source_for(exe)
            made.append(src)
            return src

        def rename(a, b):
            seen.append(made[0].kfc._res_file is None or made[0].kfc._res_file.closed)
            return real(a, b)
        snapshot.os.rename = rename
        try:
            snapshot.make(self.game, factory)
        finally:
            snapshot.os.rename = real
        self.assertEqual(seen, [True], "the resource file is closed when the directory is renamed")

    def test_older_keys_stay_until_the_new_one_reads_back(self):
        self.make()
        old = self.key()
        set_version(self.game / "enshrouded_server.kfc", b"2|^/test|2026-11-05")

        def boom(kind, tmp):
            raise RuntimeError("stop")
        with self.assertRaises(RuntimeError):
            self.make(after_kind=boom)
        self.assertTrue(snapshot.valid(self.root / old, old), "a failed run removes nothing")
        real = snapshot.valid
        snapshot.valid = lambda path, key: False if Path(path).name == key and key != old else real(path, key)
        try:
            with self.assertRaisesRegex(snapshot.SnapshotError, "does not read back"):
                self.make()
        finally:
            snapshot.valid = real
        self.assertTrue(real(self.root / old, old), "a new snapshot that does not read back removes nothing")
        self.assertEqual(self.make()["status"], "made")
        self.assertEqual([p.name for p in self.root.iterdir()], [self.key()])

    def test_client_side_and_no_exe(self):
        c = make_game(Path(self.tmp.name) / "client", name="enshrouded")
        r = snapshot.make(c, source_for)
        self.assertEqual(json.loads((Path(r["path"]) / "index.json").read_text())["side"], "client")
        with self.assertRaisesRegex(snapshot.SnapshotError, "neither"):
            snapshot.make(Path(self.tmp.name), source_for)


class CliTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.game = make_game(Path(self.tmp.name) / "game")
        self._extract = cli.refl.extract
        cli.refl.extract = lambda exe: DUMP

    def tearDown(self):
        cli.refl.extract = self._extract
        self.tmp.cleanup()

    def run_cli(self, *args):
        import contextlib
        import io
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            rc = cli.main(list(args))
        return rc, out.getvalue(), err.getvalue()

    def test_make_show_key(self):
        rc, out, _ = self.run_cli("make", "--game", str(self.game), "--json")
        self.assertEqual(rc, 0)
        r = json.loads(out)
        self.assertEqual(r["status"], "made")
        rc, out, _ = self.run_cli("make", "--game", str(self.game), "--json")
        self.assertEqual(json.loads(out)["status"], "reused")
        rc, out, _ = self.run_cli("key", "--game", str(self.game))
        self.assertEqual(out.strip(), r["key"])
        rc, out, _ = self.run_cli("show", "--game", str(self.game), "item:Material_T1_Wood_Logs", "skill:0x8EFCC472")
        self.assertEqual(rc, 0)
        dec, at, values = json.JSONDecoder(), 0, []
        while at < len(out):
            v, at = dec.raw_decode(out, at)
            values.append(v)
            while at < len(out) and out[at].isspace():
                at += 1
        self.assertEqual(values[0]["value"], EXPECTED_ITEM)
        self.assertEqual(values[1]["value"], {"id": 0x8efcc472, "cost": 3})
        rc, out, _ = self.run_cli("show", "--game", str(self.game), "item:Nope")
        self.assertEqual((rc, json.loads(out)), (1, {"query": "item:Nope", "found": False}))

    def test_if_needed_skips_without_a_boot_mod_and_makes_with_one(self):
        mods = self.game / "kiln" / "mods"

        def mod(folder, text):
            (mods / folder).mkdir(parents=True, exist_ok=True)
            (mods / folder / "kiln.mod.json").write_text(text)
        mod("plain", '{"id": "plain", "version": "1.0.0", "apiVersion": "0.2"}')
        mod("client", '{"id": "client-only", "version": "1.0.0", "apiVersion": "0.2", "boot": true, "side": "client"}')
        mod("off", '{"id": "off", "version": "1.0.0", "apiVersion": "0.2", "boot": "required"}')
        (mods / "off" / ".disabled").write_text("")
        mod("other", '{"id": "other", "boot": "yes"}')
        rc, out, err = self.run_cli("make", "--game", str(self.game), "--if-needed", "--json")
        self.assertEqual((rc, json.loads(out)), (0, {"status": "skipped", "reason": "no mod needs the boot snapshot", "emptyKinds": []}))
        self.assertFalse((self.game / "kiln" / "data").exists(), "nothing ran, nothing was written")
        rc, out, _ = self.run_cli("make", "--game", str(self.game), "--if-needed")
        self.assertEqual((rc, out.strip()), (0, "boot-snapshot: no mod needs the boot snapshot"))
        # a boot mod (with a comment line and a trailing comma, as kiln.mod.json allows): made
        mod("booter", '{\n  // OnBoot\n  "id": "booter", "version": "1.0.0", "apiVersion": "0.2", "boot": true,\n}')
        rc, out, err = self.run_cli("make", "--game", str(self.game), "--if-needed", "--json")
        self.assertEqual((rc, json.loads(out)["status"]), (0, "made"))
        self.assertIn("needed by booter", err)
        # a skip later leaves the snapshot alone
        (mods / "booter" / ".disabled").write_text("")
        key = json.loads(out)["key"]
        rc, out, _ = self.run_cli("make", "--game", str(self.game), "--if-needed", "--json")
        self.assertEqual(json.loads(out)["status"], "skipped")
        self.assertTrue(snapshot.valid(self.game / "kiln" / "data" / "boot-gamedata" / key, key), "an existing snapshot is not deleted")
        # a client counts its client-only boot mods; a server does not (above)
        self.assertEqual(snapshot.boot_mods(self.game, server=False), ["client-only"])
        self.assertEqual(snapshot.boot_mods(self.game, server=True), [])

    def test_refusal(self):
        rc, out, err = self.run_cli("make", "--game", str(Path(self.tmp.name) / "nothing"), "--json")
        self.assertEqual(rc, 1)
        self.assertEqual(json.loads(out)["status"], "refused")
        self.assertIn("neither", err)


def _driver(game, mark):
    """For test_a_killed_run_leaves_no_readable_snapshot: writes the items, says so, then waits to be killed."""
    def pause(kind, tmp):
        if kind == "items":
            Path(mark).write_text("1")
            time.sleep(60)
    snapshot.make(Path(game), source_for, after_kind=pause)


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "--driver":
        _driver(sys.argv[2], sys.argv[3])
    else:
        unittest.main()
