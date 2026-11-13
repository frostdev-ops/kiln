# SPDX-License-Identifier: MIT
"""tools/kfc tests on synthetic fixtures (no game files): KFC4 directory + zstd stream + .dat, reflection records in a
synthetic PE, reflected decode/encode, vertex codecs.

    python3 -m unittest discover -s tools/kfc/tests -v
"""
import struct
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from kfc import mesh, refl  # noqa: E402
from kfc.archive import ContentHash, Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.util import fnv1a32, guid_bytes, guid_text  # noqa: E402

try:
    from compression import zstd
except ImportError:  # pragma: no cover
    zstd = None


# ---------------------------------------------------------------- synthetic KFC4
def build_kfc(dirpath, resources, contents, chunk_size=64):
    """resources: [(guid text, type hash, part, bytes)], contents: [bytes | (hash 4-tuple, bytes)]. Writes test.kfc, test.kfc_resources,
    test_000.dat, test_001.dat; returns {content index: ContentHash}. Small chunks so resources span chunk borders."""
    d = Path(dirpath)
    stream = bytearray()
    rvals = []
    for _, _, _, data in resources:
        while len(stream) % 16:
            stream.append(0)
        rvals.append((len(stream), len(data)))
        stream += data
    chunks = []
    packed = bytearray()
    for off in range(0, len(stream), chunk_size):
        raw = bytes(stream[off:off + chunk_size])
        z = zstd.compress(raw)
        foff = len(packed)
        packed += z
        stored = (len(z) + 4095) // 4096 * 4096
        packed += b"\0" * (stored - len(z))
        chunks.append((foff, stored, len(z), off, len(raw)))
    (d / "test.kfc_resources").write_bytes(bytes(packed))
    # contents into two containers, 4 KiB aligned
    dats = [bytearray(), bytearray()]
    cvals, ckeys = [], []
    for i, c in enumerate(contents):
        h = None
        if isinstance(c, tuple):  # (ContentHash, bytes): a blob under a given hash
            h, c = ContentHash(*c[0]), c[1]
        dat = dats[i % 2]
        off = len(dat)
        dat += c + b"\0" * ((4096 - len(c) % 4096) % 4096)
        h = h or ContentHash(len(c), 0x1000 + i, 0x2000 + i, 0x3000 + i)
        ckeys.append(h)
        cvals.append((off, 0, i % 2))
    for i, dat in enumerate(dats):
        (d / f"test_{i:03d}.dat").write_bytes(bytes(dat))
    types = sorted({r[1] for r in resources})
    order = []
    bundles = []
    for th in types:
        first = len(order)
        order += [i for i, r in enumerate(resources) if r[1] == th]
        bundles.append((th, 0xABC00000 | th & 0xFFFF, first, len(order) - first))
    version = b"1|^/test|2026-11-04"
    tables = [
        ("version", version),
        ("containers", b"".join(struct.pack("<QQ", len(x), 1) for x in dats)),
        ("unused0", b""), ("unused1", b""),
        ("resloc", struct.pack("<5I", len(stream), len(packed), len(resources), 0, len(chunks))),
        ("order", struct.pack(f"<{len(order)}I", *order)),
        ("cbuckets", struct.pack("<II", 0, len(ckeys))),
        ("ckeys", b"".join(k.raw() for k in ckeys)),
        ("cvalues", b"".join(struct.pack("<IHH8x", *v) for v in cvals)),
        ("rbuckets", struct.pack("<II", 0, len(resources))),
        ("rkeys", b"".join(guid_bytes(g) + struct.pack("<IIII", th, part, 0, 0) for g, th, part, _ in resources)),
        ("rvalues", b"".join(struct.pack("<II", *v) for v in rvals)),
        ("bbuckets", struct.pack("<II", 0, len(bundles))),
        ("bkeys", b"".join(struct.pack("<I", b[0]) for b in bundles)),
        ("bvalues", b"".join(struct.pack("<3I", *b[1:]) for b in bundles)),
        ("chunks", b"".join(struct.pack("<5I", *c) for c in chunks)),
    ]
    counts = {"version": len(version), "containers": 2, "unused0": 0, "unused1": 0, "resloc": 1, "order": len(order),
              "cbuckets": 1, "ckeys": len(ckeys), "cvalues": len(ckeys), "rbuckets": 1, "rkeys": len(resources),
              "rvalues": len(resources), "bbuckets": 1, "bkeys": len(bundles), "bvalues": len(bundles),
              "chunks": len(chunks)}
    head = bytearray(b"KFC4" + b"\0" * 12 + b"\0" * 8 * len(tables))
    body = bytearray()
    pos = len(head)
    for i, (name, data) in enumerate(tables):
        at = 0x10 + 8 * i
        start = pos + len(body)
        struct.pack_into("<II", head, at, (start - at) if data else 0, counts[name])
        body += data
        while len(body) % 4:
            body.append(0)
    out = head + body
    struct.pack_into("<II", out, 4, len(out), 12)
    (d / "test.kfc").write_bytes(bytes(out))
    return ckeys


class ZstdBackendTests(unittest.TestCase):
    """A Python older than 3.14 without the zstandard package reads the game's archives through the system's
    libzstd (ctypes): the path a player's Linux machine takes."""

    def test_a_backend_is_named(self):
        from kfc import archive
        self.assertIn(archive.zstd_backend(), ("stdlib", "zstandard", "libzstd"))

    @unittest.skipIf(zstd is None, "needs Python 3.14 compression.zstd to make the frames")
    def test_the_system_library_decompresses_what_the_stdlib_does(self):
        from kfc import archive
        lib = archive._system_libzstd()
        if not lib:
            self.skipTest("no libzstd on this machine")
        for raw in (b"", b"a", b"blob zero" * 1000, bytes(range(256)) * 300):
            self.assertEqual(archive._libzstd_decompress(lib, zstd.compress(raw), 0), raw)
        # a frame without its content size (a streamed one): the hint, or the default, bounds the output
        streamed = zstd.ZstdCompressor().compress(b"x" * 5000, zstd.ZstdCompressor.FLUSH_FRAME)
        self.assertEqual(archive._libzstd_decompress(lib, streamed, 5000), b"x" * 5000)
        with self.assertRaises(RuntimeError):
            archive._libzstd_decompress(lib, b"not a frame at all", 0)
        with self.assertRaises(RuntimeError):
            archive._libzstd_decompress(lib, zstd.compress(b"y" * 4000)[:-8], 0)

    def test_without_any_decoder_the_error_says_what_to_install(self):
        from kfc import archive
        saved = archive._zstd, archive._zstandard, archive._libzstd
        archive._zstd, archive._zstandard, archive._libzstd = None, None, False
        try:
            self.assertIsNone(archive.zstd_backend())
            with self.assertRaisesRegex(RuntimeError, "libzstd"):
                archive.zstd_decompress(b"x")
        finally:
            archive._zstd, archive._zstandard, archive._libzstd = saved


@unittest.skipIf(zstd is None, "needs Python 3.14 compression.zstd")
class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.res = [
            ("1d622bd9-8170-41b1-822e-d3984b386d95", 0xB5CE8765, 0, bytes(range(200))),
            ("9c38c7f0-fd0a-41a6-b91e-9a16287b8f59", 0x177394C7, 0, b"R" * 150),
            ("1d622bd9-8170-41b1-822e-d3984b386d95", 0xF9E86997, 0, b"icon"),
            ("9c38c7f0-fd0a-41a6-b91e-9a16287b8f59", 0x177394C7, 3, b"part three"),
        ]
        self.contents = [b"blob zero" * 100, b"\x01\x02\x03", b"x" * 5000]
        self.hashes = build_kfc(self.tmp.name, self.res, self.contents)
        self.k = Kfc(self.tmp.name)

    def tearDown(self):
        self.k.close()
        self.tmp.cleanup()

    def test_header(self):
        s = self.k.summary()
        self.assertEqual(s["version"], "1|^/test|2026-11-04")
        self.assertEqual(s["resources"], 4)
        self.assertEqual(s["contents"], 3)
        self.assertEqual(s["containers"], 2)
        self.assertGreater(s["chunks"], 3)

    def test_resources_across_chunks(self):
        for g, th, part, data in self.res:
            self.assertEqual(self.k.read(ResourceKey(g, th, part)), data)

    def test_bundles_and_guid_index(self):
        rm = self.k.resources_of_type(0x177394C7)
        self.assertEqual(sorted(k[2] for k in rm), [0, 3])
        self.assertEqual(len(self.k.by_guid("1d622bd9-8170-41b1-822e-d3984b386d95")), 2)
        self.assertEqual(self.k.bundles[0x177394C7]["layout"], 0xABC094C7)

    def test_contents(self):
        for h, c in zip(self.hashes, self.contents):
            self.assertEqual(self.k.read_content(h), c)
            self.assertEqual(self.k.read_content(str(h)), c)  # text form round trip

    def test_unpatched_reads_the_archive_a_mod_loader_kept_aside(self):
        d = Path(self.tmp.name)
        self.assertIsNone(Kfc.unpatched(d), "no backup beside it: the archive is the game's own")
        self.assertIsNone(Kfc.unpatched(d / "test.kfc"))
        # A mod loader patches the archive the way EML does: the originals stay beside it as .bak, and the installed
        # index and resources now hold a mod's resource in place of the game's.
        (d / "test.kfc").rename(d / "test.kfc.bak")
        (d / "test.kfc_resources").rename(d / "test.kfc_resources.bak")
        dats = {f.name: f.read_bytes() for f in d.glob("test_*.dat")}
        patched = list(self.res)
        patched[1] = (patched[1][0], patched[1][1], 0, b"a mod's material, another shape" * 9)
        build_kfc(d, patched, self.contents)
        for name, data in dats.items():  # the .dat files only grow: the game's contents are still where they were
            self.assertEqual((d / name).read_bytes()[:len(data)], data)
        installed, own = Kfc(d), Kfc.unpatched(d)
        self.addCleanup(installed.close)
        self.addCleanup(own.close)
        key = ResourceKey(*self.res[1][:3])
        self.assertEqual(installed.read(key), patched[1][3])
        self.assertEqual(own.read(key), self.res[1][3], "the game's own resource, from the files kept aside")
        self.assertEqual(own.path.name, "test.kfc.bak")
        for g, th, part, data in self.res:
            self.assertEqual(own.read(ResourceKey(g, th, part)), data)
        for h, c in zip(self.hashes, self.contents):
            self.assertEqual(own.read_content(h), c)  # contents come from the same .dat files
        self.assertEqual(Kfc.unpatched(d / "test.kfc").read(key), self.res[1][3])
        # half a backup is no backup
        (d / "test.kfc_resources.bak").unlink()
        self.assertIsNone(Kfc.unpatched(d))
        self.assertIsNone(Kfc.unpatched(d / "nothing-here"))
        self.assertEqual(self.k.content_location(self.hashes[1])["container"], 1)


# ---------------------------------------------------------------- reflection
def build_pe(path, records):
    """A PE32+ with .rdata (names) and .data (type records). records: [{name, size, align, kind, inner, fields:
    [(name, type name, offset)], values: [(name, value)]}]. Returns nothing; layout per kfc/refl.py."""
    base = 0x140000000
    rdata_rva, data_rva = 0x2000, 0x10000
    rdata = bytearray()
    strs = {}

    def s(text):
        if text not in strs:
            strs[text] = base + rdata_rva + len(rdata)
            rdata.extend(text.encode() + b"\0")
        return strs[text]

    rec_rva = {r["name"]: data_rva + 0x80 * i for i, r in enumerate(records)}
    extra = data_rva + 0x80 * len(records) + 0x100  # field / enum tables
    data = bytearray(extra - data_rva)
    tables = bytearray()
    for i, r in enumerate(records):
        o = 0x80 * i
        qn = s(r["name"])
        short = s(r["name"].split("::")[-1])
        struct.pack_into("<QQQQQQ", data, o, short, len(r["name"].split("::")[-1]), short, 0, qn, len(r["name"]))
        inner = base + rec_rva[r["inner"]] if r.get("inner") else 0
        struct.pack_into("<Q", data, o + 0x38, inner)
        fields, values = r.get("fields", []), r.get("values", [])
        struct.pack_into("<IHHII", data, o + 0x40, r["size"], r["align"], 0, len(fields) or len(values), r["kind"])
        struct.pack_into("<II", data, o + 0x50, fnv1a32(r["name"]) ^ (1 if r.get("bad_hash") else 0), 0x11110000 + i)
        if fields:
            struct.pack_into("<Q", data, o + 0x58, base + extra + len(tables))
            for fn, ft, fo in fields:
                tables += struct.pack("<QQQQQQ", s(fn), len(fn), base + rec_rva[ft], fo, 0, 0)
        if values:
            struct.pack_into("<Q", data, o + 0x60, base + extra + len(tables))
            for vn, vv in values:
                tables += struct.pack("<QQQQQ", s(vn), len(vn), vv, 0, 0)
    data += tables
    secs = [(".rdata", rdata_rva, bytes(rdata)), (".data", data_rva, bytes(data))]
    pe_off = 0x80
    hdr = bytearray(0x400)
    hdr[0:2] = b"MZ"
    struct.pack_into("<I", hdr, 0x3C, pe_off)
    hdr[pe_off:pe_off + 4] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", hdr, pe_off + 4, 0x8664, len(secs), 0, 0, 0, 240, 0x22)
    struct.pack_into("<H", hdr, pe_off + 24, 0x20B)
    struct.pack_into("<Q", hdr, pe_off + 24 + 24, base)
    so = pe_off + 24 + 240
    raw = bytearray(hdr)
    for name, rva, blob in secs:
        struct.pack_into("<8sIIII", raw, so, name.encode(), len(blob), rva, len(blob), len(raw))
        so += 40
        raw += blob + b"\0" * ((0x200 - len(blob) % 0x200) % 0x200)
    Path(path).write_bytes(bytes(raw))


TYPES = [
    {"name": "keen::uint8", "size": 1, "align": 1, "kind": refl.K_U8},
    {"name": "keen::uint16", "size": 2, "align": 2, "kind": refl.K_U16},
    {"name": "keen::uint32", "size": 4, "align": 4, "kind": refl.K_U32},
    {"name": "keen::float32", "size": 4, "align": 4, "kind": refl.K_F32},
    {"name": "keen::float", "size": 4, "align": 4, "kind": refl.K_TYPEDEF, "inner": "keen::float32"},
    {"name": "keen::Colour", "size": 1, "align": 1, "kind": refl.K_ENUM, "inner": "keen::uint8",
     "values": [("Red", 0), ("Green", 1), ("Blue", 5)]},
    {"name": "keen::Bitmask8<keen::Colour>", "size": 1, "align": 1, "kind": refl.K_BM8, "inner": "keen::Colour"},
    {"name": "keen::Guid", "size": 16, "align": 4, "kind": refl.K_GUID},
    {"name": "keen::string", "size": 8, "align": 4, "kind": refl.K_BLOB_STRING},
    {"name": "keen::Pair", "size": 8, "align": 4, "kind": refl.K_STRUCT,
     "fields": [("a", "keen::uint32", 0), ("b", "keen::float", 4)]},
    {"name": "keen::BlobArray<keen::Pair>", "size": 8, "align": 4, "kind": refl.K_BLOB_ARRAY, "inner": "keen::Pair"},
    {"name": "keen::BlobArray<keen::uint16>", "size": 8, "align": 4, "kind": refl.K_BLOB_ARRAY, "inner": "keen::uint16"},
    {"name": "keen::BlobOptional<keen::Pair>", "size": 4, "align": 4, "kind": refl.K_BLOB_OPTIONAL, "inner": "keen::Pair"},
    {"name": "keen::Base", "size": 4, "align": 4, "kind": refl.K_STRUCT, "fields": [("id", "keen::uint32", 0)]},
    {"name": "keen::Derived", "size": 12, "align": 4, "kind": refl.K_STRUCT, "inner": "keen::Base",
     "fields": [("name", "keen::string", 4)]},
    {"name": "keen::BlobVariant<keen::Base>", "size": 12, "align": 4, "kind": refl.K_BLOB_VARIANT, "inner": "keen::Base"},
    {"name": "keen::StaticArray<keen::uint16,3>", "size": 6, "align": 2, "kind": refl.K_STATIC_ARRAY,
     "inner": "keen::uint16", "count": 3},
    {"name": "keen::Thing", "size": 72, "align": 4, "kind": refl.K_STRUCT, "fields": [
        ("guid", "keen::Guid", 0), ("colour", "keen::Colour", 16), ("mask", "keen::Bitmask8<keen::Colour>", 17),
        ("tri", "keen::StaticArray<keen::uint16,3>", 18), ("pairs", "keen::BlobArray<keen::Pair>", 24),
        ("name", "keen::string", 32), ("opt", "keen::BlobOptional<keen::Pair>", 40),
        ("var", "keen::BlobVariant<keen::Base>", 44), ("shorts", "keen::BlobArray<keen::uint16>", 56),
        ("scale", "keen::float", 64), ("none", "keen::BlobOptional<keen::Pair>", 68)]},
    {"name": "keen::Broken", "size": 4, "align": 4, "kind": refl.K_STRUCT, "bad_hash": True},
]


class ReflectionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        # static arrays keep their count in +0x48: the builder writes len(fields)|len(values); patch below
        recs = [dict(t) for t in TYPES]
        cls.exe = Path(cls.tmp.name) / "game.exe"
        build_pe(cls.exe, recs)
        b = bytearray(cls.exe.read_bytes())
        # set the StaticArray count (+0x48) by hand: find its record by name order
        pe = refl.PeImage(cls.exe)
        for rva, name in refl.scan(pe).items():
            if name.startswith("keen::StaticArray"):
                sec_va, _ = pe.sections[".data"]
                raw_off = struct.unpack_from("<I", b, 0x80 + 24 + 240 + 40 + 20)[0]  # .data raw offset
                struct.pack_into("<I", b, raw_off + (rva - sec_va) + 0x48, 3)
        cls.exe.write_bytes(bytes(b))
        cls.dump = refl.extract(cls.exe)
        cls.t = refl.TypeDb(cls.dump)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_records_found_and_bad_hash_rejected(self):
        names = {t["name"] for t in self.dump["types"]}
        self.assertIn("keen::Thing", names)
        self.assertNotIn("keen::Broken", names)
        self.assertEqual(len(names), len(TYPES) - 1)

    def test_fields_enums_inner(self):
        thing = self.t.get("keen::Thing")
        self.assertEqual(thing["hash"], fnv1a32("keen::Thing"))
        self.assertEqual([f["name"] for f in thing["fields"]][:3], ["guid", "colour", "mask"])
        self.assertEqual(thing["fields"][4]["type"], "keen::BlobArray<keen::Pair>")
        self.assertEqual([v["value"] for v in self.t.get("keen::Colour")["values"]], [0, 1, 5])
        self.assertEqual(self.t.get("keen::Derived")["inner"], "keen::Base")
        self.assertEqual([f["name"] for f in self.t.all_fields(self.t.get("keen::Derived"))], ["id", "name"])
        self.assertEqual(self.t.get("keen::StaticArray<keen::uint16,3>")["count"], 3)

    def test_decode_encode_roundtrip(self):
        value = {
            "guid": "1d622bd9-8170-41b1-822e-d3984b386d95", "colour": "Blue", "mask": ["Green", "Blue"],
            "tri": [1, 2, 3], "pairs": [{"a": 7, "b": 0.5}, {"a": 8, "b": -2.0}], "name": "hello",
            "opt": {"a": 9, "b": 1.0}, "var": {"$type": "keen::Derived", "value": {"id": 42, "name": "inner"}},
            "shorts": [10, 20, 30], "scale": 3.0, "none": None,
        }
        enc = Encoder(self.t)
        data = enc.encode("keen::Thing", value)
        back = Decoder(self.t).decode("keen::Thing", data)
        self.assertEqual(back, value)
        self.assertEqual(enc.encode("keen::Thing", back), data)
        # layout: pairs block right behind the 72-byte root, offsets self-relative
        rel, n = struct.unpack_from("<iI", data, 24)
        self.assertEqual((24 + rel, n), (72, 2))
        h, vrel, vsize = struct.unpack_from("<IiI", data, 44)
        self.assertEqual((h, vsize), (fnv1a32("keen::Derived"), 12))
        self.assertEqual(struct.unpack_from("<I", data, 48 + vrel)[0], 42)  # variant data relative to its offset field
        self.assertEqual(data[17], 0b100010)  # mask: bit indices 1 and 5
        self.assertEqual(struct.unpack_from("<i", data, 68)[0], 0)  # empty optional


# ---------------------------------------------------------------- helpers and vertex codecs
class UtilTests(unittest.TestCase):
    def test_fnv_and_guid(self):
        self.assertEqual(fnv1a32("keen::RenderModel"), 0x177394C7)
        self.assertEqual(fnv1a32("keen::ecs::GameSettings"), 0x89039810)
        g = "1d622bd9-8170-41b1-822e-d3984b386d95"
        self.assertEqual(guid_bytes(g).hex(), "d92b621d7081b141822ed3984b386d95")
        self.assertEqual(guid_text(guid_bytes(g)), g)
        self.assertEqual(fnv1a32(guid_bytes(g)), 0x43108313)  # the Bronze Sword's ItemId (EMF, kiln.content)

    def test_pos21_bit_layout(self):
        sc, of = (1.0, 1.0, 1.0), (0.0, 0.0, 0.0)
        q = mesh.Q21
        lo, hi = mesh.pack_pos21((1.0, 0.0, 0.0), sc, of)
        self.assertEqual((lo, hi), (q << 11, 0))           # x: lo bits 11..31
        lo, hi = mesh.pack_pos21((0.0, 0.0, 1.0), sc, of)
        self.assertEqual((lo, hi), (0, q))                  # z: hi bits 0..20
        lo, hi = mesh.pack_pos21((0.0, 1.0, 0.0), sc, of)
        self.assertEqual((lo, hi), (0x7FF, 0x3FF << 21))    # y: high 11 bits in lo 0..10, low 10 bits in hi 21..30
        p = (0.25, 0.6, 0.9)
        back = mesh.unpack_pos21(*mesh.pack_pos21(p, (2, 2, 2), (-1, -1, -1)), (2, 2, 2), (-1, -1, -1))
        for a, b in zip(p, back):
            self.assertAlmostEqual(a, b, places=5)

    def test_1010102(self):
        u = mesh.pack_1010102(1.0, -1.0, 0.0, -1)
        self.assertEqual(u >> 30, 3)
        x, y, z, w = mesh.unpack_1010102(u)
        self.assertEqual((round(x, 3), round(y, 3), round(z, 3), w), (1.0, -1.0, 0.0, -1.0))
        self.assertEqual(mesh.unpack_1010102(mesh.pack_1010102(0, 0, 1, 1))[3], 1.0)

    def test_triangles_from_synthetic_render_model(self):
        P = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.5)]
        sc, of = (2.0, 2.0, 2.0), (-0.5, -0.5, -0.5)
        vb = b"".join(struct.pack("<II", *mesh.pack_pos21(p, sc, of)) for p in P)
        vb += struct.pack("<3I", *[mesh.pack_1010102(0, 0, -1, 1)] * 3)
        vb += struct.pack("<3I", *[mesh.pack_1010102(1, 0, 0, -1)] * 3)
        vb += b"\xff" * 12
        vb += b"".join(struct.pack("<ee", u, v) for u, v in ((0, 0), (1, 0), (0, 1)))
        ib = struct.pack("<3H", 0, 2, 1)
        desc = {"positionScale": dict(zip("xyz", sc)), "positionOffset": dict(zip("xyz", of)),
                "vertexData": {"start": 0, "count": len(vb)}, "indexData": {"start": len(vb), "count": 6},
                "materials": [{"material": "00000000-0000-0000-0000-000000000001"}],
                "lods": [{"meshOffset": 0, "meshCount": 1, "pixelSize": 0.0}],
                "meshes": [{"vertexFormat": "PNTCT", "vertexStride": 24, "positionQuantization": "x21y21z21_unorm",
                            "vertexCount": 3, "vertexDataByteOffset": 0, "indexOffset": 0, "indexCount": 3,
                            "materialIndex": 0}]}
        tri = mesh.triangles(mesh.RenderModel("t", desc, vb + ib), 0)
        s = tri.submeshes[0]
        for a, b in zip(P, s["positions"]):
            for i in range(3):
                self.assertAlmostEqual(a[i], b[i], places=5)
        self.assertEqual(s["indices"], [0, 2, 1])
        self.assertEqual(s["uvs"][1], (1.0, 0.0))
        rh = mesh.to_rh(tri).submeshes[0]
        self.assertAlmostEqual(rh["positions"][1][0], -1.0, places=5)
        self.assertAlmostEqual(rh["positions"][2][2], 0.5, places=5)
        self.assertEqual(rh["tangents"][0][3], 1.0)

class CliRowTests(unittest.TestCase):
    """find-items / find-templates rows: ids decode as ints or as one-field structs depending on the build."""

    def test_scalar_forms(self):
        from kfc import cli
        self.assertEqual(cli.scalar(0x43108313), 0x43108313)
        self.assertEqual(cli.scalar({"value": 0x43108313}), 0x43108313)
        self.assertEqual(cli.scalar({"id": {"value": 7}}), 7)
        self.assertIsNone(cli.scalar({"a": 1, "b": 2}))
        self.assertIsNone(cli.scalar("x"))
        self.assertEqual(cli.hex_id({"value": 0xe988b200}), "0xe988b200")
        self.assertIsNone(cli.hex_id(None))

    def test_item_row(self):
        from kfc import cli
        v = {"debugName": "Ammo_T1_Spell_Fire_Ball_lvl10", "itemId": {"value": 0xe988b200}, "iconModel": "g1",
             "equipment": {"visualModel": "g2", "visualEntity": "g3", "equipmentEntityModelSetup": {"model": "g4"}}}
        r = cli.item_row("ffdf27a5-6283-4e40-916b-38ba99646dd6", v)
        self.assertEqual(r["itemId"], "0xe988b200")
        self.assertEqual((r["visualModel"], r["visualEntity"], r["equipmentModel"]), ("g2", "g3", "g4"))
        r2 = cli.item_row("x", {"debugName": "A", "itemId": 5, "equipment": None})
        self.assertEqual(r2["itemId"], "0x00000005")
        self.assertIsNone(r2["visualModel"])

    def test_template_row_and_blob_string(self):
        from kfc import cli
        data = struct.pack("<iI", 8, 5) + b"Vukah"
        self.assertEqual(cli.blob_string_at(data, 0), "Vukah")
        v = {"name": "Enemy_Vukah_Melee", "family": "None",
             "components": [{"$type": "keen::ecs::Faction", "value": {}}, None]}
        r = cli.template_row("d5d05217-52dc-467a-9192-26b4f661d8d9", v, components=True)
        self.assertEqual(r["components"], ["Faction", "?"])


if __name__ == "__main__":
    unittest.main()


class SkinTests(unittest.TestCase):
    def test_skin_stream_and_skeleton(self):
        # two vertices: joint indices go through skinningMap, weights are u8 / 255
        skin = bytes([0, 1, 0, 0, 200, 55, 0, 0, 1, 0, 0, 0, 255, 0, 0, 0])
        desc = {"skinningData": {"start": 0, "count": 16}, "skinningMap": [7, 9]}
        m = mesh.RenderModel("t", desc, skin)
        joints, weights = mesh.skin_of(m, {"skinVertexOffset": 0, "vertexCount": 2})
        self.assertEqual(joints, [(7, 9, 7, 7), (9, 7, 7, 7)])
        self.assertAlmostEqual(sum(weights[0]), 1.0)
        h = {"jointCount": 2, "jointNames": [1, 2], "jointParentIndices": [0xFFFF, 0],
             "defaultPoseJoints": [
                 {"position": {"x": 1, "y": 0, "z": 0}, "orientation": {"x": 0, "y": 0, "z": 0, "w": 1},
                  "scale": {"x": 1, "y": 1, "z": 1}},
                 {"position": {"x": 0, "y": 2, "z": 0}, "orientation": {"x": 0, "y": 0.7071068, "z": 0, "w": 0.7071068},
                  "scale": {"x": 1, "y": 1, "z": 1}}]}
        sk = mesh.skeleton_rh(h)
        self.assertEqual(sk[0]["t"], (-1, 0, 0))                     # x mirrored
        self.assertEqual(sk[1]["r"][1], -0.7071068)                   # quaternion y, z negated
        self.assertEqual([round(sk[1]["global"][i][3], 6) for i in range(3)], [-1.0, 2.0, 0.0])
        inv = mesh._inv_affine(sk[1]["global"])
        ident = mesh._mul4(inv, sk[1]["global"])
        for i in range(4):
            for j in range(4):
                self.assertAlmostEqual(ident[i][j], 1.0 if i == j else 0.0, places=6)
