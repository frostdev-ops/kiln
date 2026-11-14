# SPDX-License-Identifier: MIT
"""tools/bootsnap tests on a synthetic archive and type graph the test builds (no game files): the decoder core.

    python3 -m unittest discover -s tools/bootsnap/tests -v

The expected JSON objects below are written by hand from the live reader's rules (native/services/content/
gamedata_walk.cpp node_json / leaf_json / leaf_of, depth 12), not produced by the code under test.
"""
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOOL = HERE.parent
sys.path.insert(0, str(TOOL))
sys.path.insert(0, str(TOOL.parent / "kfc"))
sys.path.insert(0, str(TOOL.parent / "kfc" / "tests"))

from bootdata import kinds, live  # noqa: E402
from kfc.archive import Kfc  # noqa: E402
from kfc.util import guid_bytes  # noqa: E402
from test_kfc import build_kfc  # noqa: E402  (tools/kfc's own synthetic KFC4 writer, read-only reuse)


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


def source_for(exe):
    """kinds.Source over the synthetic graph."""
    graph = live.Graph(DUMP)
    k = Kfc.unpatched(exe.with_suffix(".kfc")) or Kfc(exe.with_suffix(".kfc"))
    configs = live.ConfigNames()
    for key in k.resources_of_type(fnv("keen::ImpactProgram")):
        configs.add_program(graph, k.read(key))
    return kinds.Source(k, graph, live.Walker(graph, configs.finish()))


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
        self.src.kfc.close()
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


if __name__ == "__main__":
    unittest.main()
