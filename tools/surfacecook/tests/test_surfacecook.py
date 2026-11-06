# SPDX-License-Identifier: MIT
"""kiln-surfacecook: building surfaces, against tools/kfc and the installed client's own tables.

    python3 -m unittest discover -s tools/surfacecook/tests -v

Without the game: the surface parser, by field. With the installed client (read only; skipped when absent): its three surface tables read and
write back byte for byte, whole and slot by slot; every one of its textures has the streaming layout the cooker writes, and a sample of their
blobs hash as the tables say; then a mod's surfaces are cooked into the tables and checked by tools/kfc's own decoder and against the rules
the game's surfaces follow. Nothing from the game is written anywhere but a temp folder.

What this can't show: how a surface looks in the game. That is a manual check on a client.
"""
import json
import math
import os
import struct
import subprocess
import sys
import tempfile
import unittest
import uuid
import zlib
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO / "tools" / "kfc"))
TOOL = REPO / "tools" / "surfacecook"
GAME = Path(os.environ.get("KILN_TEST_GAME", Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"))
HAS_GAME = (GAME / "enshrouded.kfc").exists()
NS = uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77")

SLOT, LAYER, GI_SLOT, GI_FACE = 392, 60, 180, 60
MAPS = {0: "emissive", 12: "albedo", 16: "metallic", 20: "roughness", 24: "reflectance", 28: "normal", 32: "height", 36: "ao"}
FORMAT = {"emissive": "BC1_RGB_srgb_block", "albedo": "BC7_srgb_block", "normal": "BC5_unorm_block"}
SORT_LAYER = [0, 1, 2, 3, 3, 4]
CHAIN_CATEGORY, TAIL_CATEGORY = 1013777168, 1185060183
# Cell ids of the game materials the fixtures name (client 26430625): what kiln.worldtables resolves these names to.
NAMES = {"StoneRough": 155, "StoneAncientMoonstone": 212, "WoodRough": 143}


def tool():
    cached = getattr(tool, "path", None)
    if cached:
        return cached
    out = subprocess.run([str(TOOL / "native" / "build.sh")], capture_output=True, text=True)
    if out.returncode:
        raise RuntimeError("kiln-surfacecook did not build:\n" + out.stderr[-4000:])
    tool.path = out.stdout.strip().splitlines()[-1]
    return tool.path


def run(*args, ok=(0,)):
    p = subprocess.run([tool(), *map(str, args)], capture_output=True, text=True)
    if p.returncode not in ok:
        raise AssertionError(f"kiln-surfacecook {args}: {p.returncode}\n{p.stdout}\n{p.stderr}")
    return p


def png(width, height, pixel, channels=3):
    """A PNG whose pixel (x, y) is pixel(x, y): a tuple of `channels` bytes."""
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            rows += bytes(pixel(x, y))
    kind = {1: 0, 3: 2, 4: 6}[channels]
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, kind, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(bytes(rows)))
            + chunk(b"IEND", b""))


def brick(x, y):
    row = y // 32
    bx, by = (x + (32 if row % 2 else 0)) % 64, y % 32
    if bx < 3 or by < 3:
        return (110, 115, 100)
    n = (math.sin(x * 0.31) * math.cos(y * 0.17) + 1) * 0.5
    return (60, int(90 + 60 * n), 55)


def write_mod(root, mod_id, blocks, textures):
    d = Path(root) / mod_id
    (d / "blocks").mkdir(parents=True)
    (d / "kiln.mod.json").write_text(json.dumps({"id": mod_id, "name": mod_id, "version": "0.1.0", "apiVersion": "0.1", "side": "both", "clients": "mod"}))
    (d / "blocks" / "surfaces.json").write_text(json.dumps({"format": 1, **blocks}))
    for rel, data in textures.items():
        (d / rel).parent.mkdir(parents=True, exist_ok=True)
        (d / rel).write_bytes(data)
    return d


class Parser(unittest.TestCase):
    """The surface parser: what the table merge runs on every side, and the cooker before it cooks."""

    def check(self, surface, tweak=False):
        args = ["check", json.dumps(surface), "--label", "block buildingMaterial m (mod x)"] + (["--tweak", "1"] if tweak else [])
        return json.loads(run(*args, ok=(0, 1)).stdout)

    def test_a_good_surface(self):
        r = self.check({"side": {"albedo": "t/a.png", "tint": "#ABCDEF", "normal": {"map": "t/n.png", "strength": 0.5}, "roughness": 0.4, "height": "t/h.png",
                                 "emissive": {"map": "t/e.png", "strength": 3, "gi": 8}, "tiling": 2, "displacement": {"scale": 0.2, "offset": -0.1}},
                        "top": "side", "edge": {"game": "StoneRough", "layer": "edge", "albedo": "t/e2.png"}, "bevel": 0.2, "noiseRange": [0.2, 0.6],
                        "mapping": "wood", "decoration": "none", "comment": "anything"})
        self.assertTrue(r["ok"], r)

    def test_mistakes_name_the_field(self):
        r = self.check({"side": {"albedo": "a.jpg", "tint": "#12345", "roughness": 2, "tiling": 0, "bogus": 1}, "top": "top", "edge": {"layer": "edge"},
                        "noiseRange": [0.5, 0.2], "mapping": "roof", "glow": 1})
        self.assertFalse(r["ok"])
        text = "\n".join(r["errors"])
        for field in ("surface.side.albedo: \"a.jpg\" is not a .png", "surface.side.tint:", "surface.side.roughness: 2 is outside", "surface.side.tiling:",
                      "surface.side.bogus: is not a field", "surface.top:", "surface.edge.layer:", "surface.noiseRange:", "surface.mapping:", "surface.glow: is not a field"):
            self.assertIn("block buildingMaterial m (mod x), " + field, text)

    def test_paths_stay_in_the_mod(self):
        for bad in ("../x.png", "/x.png", "C:/x.png", "a/../../x.png"):
            r = self.check({"side": {"albedo": bad}})
            self.assertIn("has to be a path inside the mod's folder", "\n".join(r["errors"]), bad)

    def test_a_new_material_needs_a_side(self):
        r = self.check({"top": {"albedo": "t.png"}})
        self.assertIn("surface.side: is required", "\n".join(r["errors"]))
        r = self.check({"side": "top", "top": {"albedo": "t.png"}})
        self.assertIn("surface.side: has to be a layer of its own", "\n".join(r["errors"]))

    def test_a_retexture_writes_what_it_changes(self):
        self.assertTrue(self.check({"top": {"albedo": "t.png"}}, tweak=True)["ok"])
        self.assertTrue(self.check({"edge": "top"}, tweak=True)["ok"])
        self.assertIn("changes no layer", "\n".join(self.check({"bevel": 0.1}, tweak=True)["errors"]))
        self.assertIn("go round in a circle", "\n".join(self.check({"top": "edge", "edge": "top"}, tweak=True)["errors"]))

    def test_the_normal_form_ignores_how_it_was_written(self):
        a = self.check({"side": {"albedo": "a.png", "tint": "#ABCDEF", "normal": "n.png", "emissive": "e.png", "tiling": 2}})
        b = self.check({"side": {"tiling": 2.0, "emissive": {"map": "e.png"}, "normal": {"map": "n.png"}, "tint": "#abcdef", "albedo": "a.png"}})
        self.assertEqual(a["normal"], b["normal"])
        c = self.check({"side": {"albedo": "a.png", "tiling": 3}})
        self.assertNotEqual(a["normal"], c["normal"])


@unittest.skipUnless(HAS_GAME, "the installed client is not here")
class WithTheGame(unittest.TestCase):
    """Read-only against the installed client."""

    @classmethod
    def setUpClass(cls):
        from kfc.archive import ContentHash, Kfc
        from kfc.blob import Decoder
        from kfc.blobwrite import Encoder
        from kfc.refl import TypeDb
        cls.ContentHash = ContentHash
        cls.kfc = Kfc(GAME)
        cls.types = TypeDb.from_exe(GAME / "enshrouded.exe")
        cls.dec = Decoder(cls.types, max_array=1 << 26)
        cls.enc = Encoder(cls.types)
        cls.tmp = tempfile.TemporaryDirectory(prefix="kiln-surfacecook-")
        cls.dir = Path(cls.tmp.name)
        cls.files = {}
        for short, name in (("blend", "BuildingMaterialBlendingResource"), ("gi", "GiVoxelBuildingMaterialResource"), ("params", "BuildingMaterialParametersResource")):
            t = cls.types.get("keen::" + name)
            keys = cls.kfc.resources_of_type(t["hash"])
            assert len(keys) == 1, name
            cls.files[short] = cls.dir / f"{name}.bin"
            cls.files[short].write_bytes(cls.kfc.read(keys[0]))
        cls.game = {k: cls.decode(k, v.read_bytes()) for k, v in cls.files.items()}
        # the game's mip tails, small, for the averages of a game map
        (cls.dir / "blobs").mkdir()
        for block in (cls.game["blend"]["renderingDataBlock"], cls.game["gi"]["dataBlock"]):
            for tp in block["textures"]:
                h = tp["texture"]["mipTailContent"]["hash"]
                f = cls.dir / "blobs" / ("%x-%x-%x-%x.bin" % (h["size"], h["hash0"], h["hash1"], h["hash2"]))
                if not f.exists():
                    f.write_bytes(cls.kfc.read_content(ContentHash(h["size"], h["hash0"], h["hash1"], h["hash2"])))
        (cls.dir / "names.json").write_text(json.dumps(NAMES))

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    @classmethod
    def decode(cls, short, data):
        name = {"blend": "BuildingMaterialBlendingResource", "gi": "GiVoxelBuildingMaterialResource", "params": "BuildingMaterialParametersResource"}[short]
        return cls.dec.decode(cls.types.get("keen::" + name), data)

    def tables(self):
        return ["--blend", self.files["blend"], "--gi", self.files["gi"], "--params", self.files["params"]]

    def cook(self, *mods, extra=(), ok=(0,)):
        out = Path(tempfile.mkdtemp(dir=self.dir))
        args = ["cook", *self.tables(), "--out", out, "--names", self.dir / "names.json", "--blobs", self.dir / "blobs", *extra]
        for m in mods:
            args += ["--mod", m]
        report = json.loads(run(*args, ok=ok).stdout)
        cooked = {k: self.decode(k, (out / f).read_bytes()) for k, f in (("blend", "blending.bin"), ("gi", "gi.bin"), ("params", "params.bin")) if (out / f).exists()}
        return report, cooked, out

    # ---- the game's own tables ----------------------------------------------------------------------------------------
    def test_tables_round_trip(self):
        r = json.loads(run("roundtrip", *self.tables()).stdout)
        for k in ("blending", "gi", "params", "blendingBySlot", "giBySlot", "paramsBySlot"):
            self.assertTrue(r[k]["identical"], (k, r[k]))
        self.assertEqual(r["slots"], self.game["blend"]["layerCount"])
        self.assertEqual(r["slotErrors"], [])

    def test_every_texture_has_the_cookers_layout(self):
        r = json.loads(run("layouts", "--blend", self.files["blend"], "--gi", self.files["gi"]).stdout)
        n = len(self.game["blend"]["renderingDataBlock"]["textures"]) + len(self.game["gi"]["dataBlock"]["textures"])
        self.assertEqual(r["textures"], n)
        self.assertEqual(r["disagree"], 0, r["examples"])

    def test_real_blobs_hash_as_the_tables_say(self):
        """One texture of each format and size: its chain hashes to content.hash, and its mip tail is the chain's end."""
        seen = {}
        for tp in self.game["blend"]["renderingDataBlock"]["textures"]:
            t = tp["texture"]
            seen.setdefault((t["format"], t["width"], t["height"]), t)
        for key, t in sorted(seen.items()):
            with self.subTest(key=key):
                h = t["content"]["hash"]
                chain = self.kfc.read_content(self.ContentHash(h["size"], h["hash0"], h["hash1"], h["hash2"]))
                f = self.dir / "chain.bin"
                f.write_bytes(chain)
                self.assertEqual(run("hash", f).stdout.strip(), "%x-%x-%x-%x" % (h["size"], h["hash0"], h["hash1"], h["hash2"]))
                th = t["mipTailContent"]["hash"]
                self.assertEqual(run("hash", f, "--from", len(chain) - th["size"]).stdout.strip(), "%x-%x-%x-%x" % (th["size"], th["hash0"], th["hash1"], th["hash2"]))

    # ---- cooking --------------------------------------------------------------------------------------------------------
    @classmethod
    def lab_mod(cls, root):
        tex = {
            "textures/moss.png": png(256, 256, brick),
            "textures/moss_n.png": png(256, 256, lambda x, y: (188 if x % 64 < 3 else 128, 188 if y % 32 < 3 else 128, 255)),
            "textures/moss_r.png": png(256, 256, lambda x, y: (240 if x % 64 < 3 or y % 32 < 3 else 170,), channels=1),
            "textures/moss_h.png": png(256, 256, lambda x, y: (40 if x % 64 < 3 or y % 32 < 3 else 200,), channels=1),
            "textures/top.png": png(128, 128, lambda x, y: (120, 160, 90)),
            "textures/snow.png": png(128, 128, lambda x, y: (235, 240, 250)),
            "textures/runes.png": png(64, 64, lambda x, y: (40, 200, 255) if (x // 8 + y // 8) % 2 else (0, 0, 0)),
        }
        blocks = {
            "surfaces": {"look": {"side": {"albedo": "textures/moss.png"}, "top": {"albedo": "textures/top.png"}, "bevel": 0.3}},
            "buildingMaterials": {
                "named-a": {"name": "Named A", "surface": "look"},
                "named-b": {"name": "Named B", "surface": "look", "icon": "icons/own.png"},
                "moss-brick": {"name": "Moss Brick", "strength": {"hardness": "hard", "health": 1200, "breaks": "stone"},
                               "surface": {"side": {"albedo": "textures/moss.png", "normal": "textures/moss_n.png", "roughness": "textures/moss_r.png",
                                                    "height": "textures/moss_h.png"},
                                           "top": {"albedo": "textures/top.png", "tiling": 2}, "edge": {"game": "StoneAncientMoonstone"}, "bevel": 0.2}},
                "moon-copy": {"name": "Moon Copy", "surface": {"side": {"game": "StoneAncientMoonstone"}}},
                "rune-stone": {"name": "Rune Stone", "surface": {"side": {"game": "StoneRough", "emissive": {"map": "textures/runes.png", "strength": 3}},
                                                                 "decoration": "none"}},
                "plain": {"name": "Plain"},
                "roof-tile": {"name": "Roof Tile", "roof": True, "surface": {"side": {"albedo": "textures/top.png"}}},
            },
            "materialTweaks": {"snowy-logs": {"material": "WoodRough", "surface": {"side": {"albedo": "textures/snow.png"}}}},
        }
        return write_mod(root, "surf-lab", blocks, tex)

    @classmethod
    def cooked_lab(cls):
        if not hasattr(cls, "_lab"):
            mod = cls.lab_mod(Path(tempfile.mkdtemp(dir=cls.dir)))
            out = Path(tempfile.mkdtemp(dir=cls.dir))
            args = ["cook", "--blend", cls.files["blend"], "--gi", cls.files["gi"], "--params", cls.files["params"], "--out", out, "--names", cls.dir / "names.json",
                    "--blobs", cls.dir / "blobs", "--mod", mod]
            report = json.loads(run(*args).stdout)
            raw = {k: (out / f).read_bytes() for k, f in (("blend", "blending.bin"), ("gi", "gi.bin"), ("params", "params.bin"))}
            cls._lab = (report, {k: cls.decode(k, v) for k, v in raw.items()}, raw, out, mod)
        return cls._lab

    def slot_of(self, report, block):
        return next(s["slot"] for s in report["surfaces"] if s["block"] == block)

    @staticmethod
    def patches(block, stride, slot):
        return [p for p in block["patchPoints"] if p["offset"] // stride == slot]

    def test_cooked_tables_are_canonical(self):
        """tools/kfc's decoder reads them and its encoder writes the same bytes."""
        report, cooked, raw, out, _ = self.cooked_lab()
        self.assertTrue(report["ok"], report["errors"])
        names = {"blend": "BuildingMaterialBlendingResource", "gi": "GiVoxelBuildingMaterialResource", "params": "BuildingMaterialParametersResource"}
        for k, v in cooked.items():
            self.assertEqual(self.enc.encode(self.types.get("keen::" + names[k]), v), raw[k], k)
        n = self.game["blend"]["layerCount"] + 7
        self.assertEqual(cooked["blend"]["layerCount"], n)
        self.assertEqual(cooked["gi"]["materialCount"], n)
        self.assertEqual(len(cooked["params"]["materials"]), n)
        self.assertEqual(cooked["blend"]["renderingDataBlock"]["typeSignatureHash"], self.game["blend"]["renderingDataBlock"]["typeSignatureHash"])
        self.assertEqual(len(bytes.fromhex(cooked["blend"]["renderingDataBlock"]["data"]["bytes"])), 128 * SLOT)

    def test_only_the_slots_written_change(self):
        report, cooked, *_ = self.cooked_lab()
        written = {s["slot"] for s in report["surfaces"]}
        self.assertIn(NAMES["WoodRough"] - 128, written)
        for short, key, stride in (("blend", "renderingDataBlock", SLOT), ("gi", "dataBlock", GI_SLOT)):
            gb, cb = self.game[short][key], cooked[short][key]
            gd, cd = bytes.fromhex(gb["data"]["bytes"]), bytes.fromhex(cb["data"]["bytes"])
            def tex(block, s):
                return [json.dumps(block["textures"][p["parameterIndex"]], sort_keys=True) if p["type"] == "Texture" else p["offset"] % stride for p in self.patches(block, stride, s)]
            for s in range(128):
                if s in written:
                    continue
                self.assertEqual(gd[s * stride:(s + 1) * stride], cd[s * stride:(s + 1) * stride], (short, s))
                self.assertEqual(tex(gb, s), tex(cb, s), (short, s))
        for s in range(self.game["blend"]["layerCount"]):
            if s not in written:
                self.assertEqual(self.game["params"]["materials"][s], cooked["params"]["materials"][s], s)

    def test_textures_follow_the_games_rules(self):
        from kfc.contenthash import content_hash
        report, cooked, raw, out, _ = self.cooked_lab()
        blobs = {f.stem: f for f in (out / "blobs").iterdir()}
        b = cooked["blend"]["renderingDataBlock"]
        guids = {i["block"]: uuid.UUID(i["guid"]) for i in report["ids"]}
        for s in report["surfaces"]:
            slot = s["slot"]
            mats = [p for p in self.patches(b, SLOT, slot) if p["type"] == "Texture"]
            fbs = [p for p in self.patches(b, SLOT, slot) if p["type"] == "TextureFeedback"]
            self.assertEqual(len(mats), s["textures"])
            for layer in range(6):
                lt = [p["parameterIndex"] for p in mats if (p["offset"] % SLOT) // LAYER == layer]
                lf = [p for p in fbs if (p["offset"] % SLOT) // LAYER == layer]
                self.assertEqual(len(lf), 1, (s["block"], layer))
                self.assertEqual(b["textureFeedbacks"][lf[0]["parameterIndex"]]["textureParameterIndices"], lt)
                self.assertEqual({MAPS[(p["offset"] % SLOT) % LAYER] for p in mats if (p["offset"] % SLOT) // LAYER == layer} - {"emissive"},
                                 {"albedo", "metallic", "roughness", "reflectance", "normal", "height", "ao"})
            for p in mats:
                tp = b["textures"][p["parameterIndex"]]
                t, kind = tp["texture"], MAPS[(p["offset"] % SLOT) % LAYER]
                self.assertEqual(tp["category"], "BuildingMaterial")
                self.assertEqual(t["format"], FORMAT.get(kind, "BC4_unorm_block"), kind)
                self.assertEqual(t["content"]["category"]["value"], CHAIN_CATEGORY)
                self.assertEqual(t["mipTailContent"]["category"]["value"], TAIL_CATEGORY)
                self.assertEqual(t["mipTailContent"]["bundleId"], 0)
                self.assertEqual(t["mipTailContent"]["sortKey"], t["mipTailContent"]["hash"]["hash2"])
                key = "%x-%x-%x-%x" % tuple(t["content"]["hash"][k] for k in ("size", "hash0", "hash1", "hash2"))
                if not t["debugName"].startswith("Building_kiln_"):
                    continue  # a game texture the game serves
                layer = (p["offset"] % SLOT) // LAYER
                self.assertEqual(t["content"]["sortKey"], SORT_LAYER[layer] << 8 | list(MAPS.values()).index(kind))
                owner = guids.get(s["block"], uuid.uuid5(NS, f"surf-lab:materialtweak:{s['block']}"))
                self.assertEqual(t["content"]["bundleId"], struct.unpack("<I", owner.bytes_le[:4])[0])
                # the blob is there, it is the whole chain, and the tail is its end
                chain = blobs[key].read_bytes()
                self.assertEqual(len(chain), t["content"]["hash"]["size"])
                tkey = "%x-%x-%x-%x" % tuple(t["mipTailContent"]["hash"][k] for k in ("size", "hash0", "hash1", "hash2"))
                self.assertEqual(blobs[tkey].read_bytes(), chain[len(chain) - len(blobs[tkey].read_bytes()):])
                if len(chain) < 200000:
                    h = content_hash(chain)
                    self.assertEqual(tuple(h), tuple(t["content"]["hash"][k] for k in ("size", "hash0", "hash1", "hash2")))
                # levels and ranges: the rule every game texture follows
                sizes, w, hgt = [], t["width"], t["height"]
                block = 8 if t["format"] in ("BC1_RGB_srgb_block", "BC4_unorm_block") else 16
                while True:
                    sizes.append(((w + 3) // 4) * ((hgt + 3) // 4) * block)
                    if w == 1 and hgt == 1:
                        break
                    w, hgt = max(1, w // 2), max(1, hgt // 2)
                self.assertEqual(t["levelCount"], len(sizes))
                total, cum, ranges = sum(sizes), 0, []
                for z in sizes:
                    if total - cum > 4096:
                        ranges.append({"start": cum, "count": total - cum})
                    cum += z
                self.assertEqual(t["mipChainContentRanges"], ranges)

    def test_gi_faces_use_the_layers_maps(self):
        report, cooked, *_ = self.cooked_lab()
        b, g = cooked["blend"]["renderingDataBlock"], cooked["gi"]["dataBlock"]
        for s in report["surfaces"]:
            slot = s["slot"]
            for face, layer in enumerate((1, 0, 2)):
                for gi_off, map_off in ((56, 12), (52, 0)):
                    gp = [p for p in self.patches(g, GI_SLOT, slot) if p["offset"] % GI_SLOT == face * GI_FACE + gi_off]
                    bp = [p for p in self.patches(b, SLOT, slot) if p["offset"] % SLOT == layer * LAYER + map_off]
                    self.assertEqual(len(gp), len(bp), (s["block"], face, gi_off))
                    if gp:
                        gt, bt = g["textures"][gp[0]["parameterIndex"]], b["textures"][bp[0]["parameterIndex"]]
                        self.assertEqual(gt["texture"], bt["texture"])
                        self.assertEqual(gt["category"], "GiVoxelMaterial")

    def test_a_game_surface_taken_whole_is_the_games_slot(self):
        """moon-copy is the configuration a client drew (a free slot with moonstone's maps): byte for byte."""
        report, cooked, *_ = self.cooked_lab()
        for block, game in (("moon-copy", NAMES["StoneAncientMoonstone"] - 128), ("plain", NAMES["StoneRough"] - 128)):
            mine = self.slot_of(report, block)
            for short, key, stride in (("blend", "renderingDataBlock", SLOT), ("gi", "dataBlock", GI_SLOT)):
                c, gm = cooked[short][key], self.game[short][key]
                cd, gd = bytes.fromhex(c["data"]["bytes"]), bytes.fromhex(gm["data"]["bytes"])
                self.assertEqual(cd[mine * stride:(mine + 1) * stride], gd[game * stride:(game + 1) * stride], (block, short))
                tex = lambda blk, s: [(p["offset"] % stride, blk["textures"][p["parameterIndex"]] if p["type"] == "Texture" else None) for p in self.patches(blk, stride, s)]
                self.assertEqual(tex(c, mine), tex(gm, game), (block, short))
            self.assertEqual(cooked["params"]["materials"][mine], self.game["params"]["materials"][game], block)

    def test_what_a_surface_says_is_what_is_written(self):
        report, cooked, *_ = self.cooked_lab()
        b = cooked["blend"]["renderingDataBlock"]
        d = bytes.fromhex(b["data"]["bytes"])
        f = lambda s, off: struct.unpack_from("<f", d, s * SLOT + off)[0]
        u = lambda s, off: struct.unpack_from("<I", d, s * SLOT + off)[0]
        tex = lambda s, layer, m: next(b["textures"][p["parameterIndex"]]["texture"] for p in self.patches(b, SLOT, s) if p["type"] == "Texture" and p["offset"] % SLOT == layer * LAYER + m)
        moss, moon = self.slot_of(report, "moss-brick"), NAMES["StoneAncientMoonstone"] - 128
        self.assertAlmostEqual(f(moss, 0 * LAYER + 0x2c), 0.25)   # side: once per 4 m
        self.assertAlmostEqual(f(moss, 1 * LAYER + 0x2c), 0.5)    # top: tiling 2
        self.assertAlmostEqual(f(moss, 0 * LAYER + 0x30), 0.1)    # a height map's default displacement
        self.assertAlmostEqual(f(moss, 0 * LAYER + 0x34), -0.05)
        self.assertAlmostEqual(f(moss, 0x180), 0.2)               # bevel
        self.assertEqual(tex(moss, 3, 12), self.game_tex(moon, 3, 12))   # edge: moonstone's
        self.assertEqual(tex(moss, 4, 12), self.game_tex(moon, 4, 12))   # edgeTop follows the edge
        self.assertEqual(tex(moss, 2, 12)["content"]["hash"], tex(moss, 0, 12)["content"]["hash"])  # bottom repeats the side, with its own sort key
        self.assertEqual(tex(moss, 2, 12)["content"]["sortKey"], 2 << 8 | 1)
        self.assertTrue(tex(moss, 0, 12)["debugName"].startswith("Building_kiln_surf-lab_moss-brick_side_albedo"))
        rune = self.slot_of(report, "rune-stone")
        for layer in range(6):
            self.assertEqual((f(rune, layer * LAYER + 4), f(rune, layer * LAYER + 8), f(rune, layer * LAYER + 0x38)), (3.0, 0.75, 4.0))
        self.assertEqual(cooked["params"]["materials"][rune]["decoObjectRules"], [])
        self.assertEqual(tex(rune, 0, 12), self.game_tex(NAMES["StoneRough"] - 128, 0, 12))
        roof = self.slot_of(report, "roof-tile")
        self.assertEqual(u(roof, 0x184), 1)
        self.assertEqual(cooked["params"]["materials"][roof]["mapping"], "Roof")
        logs = NAMES["WoodRough"] - 128
        self.assertTrue(tex(logs, 0, 12)["debugName"].startswith("Building_kiln_surf-lab_snowy-logs"))
        for layer in range(1, 6):
            self.assertEqual(tex(logs, layer, 12), self.game_tex(logs, layer, 12), layer)

    def game_tex(self, slot, layer, m):
        b = self.game["blend"]["renderingDataBlock"]
        return next(b["textures"][p["parameterIndex"]]["texture"] for p in self.patches(b, SLOT, slot) if p["type"] == "Texture" and p["offset"] % SLOT == layer * LAYER + m)

    def test_pixels_survive_the_encoders(self):
        """The cooked albedo, normal and roughness of moss-brick decode close to their PNGs."""
        try:
            from PIL import Image
        except ImportError:
            self.skipTest("Pillow is not installed")
        report, cooked, raw, out, mod = self.cooked_lab()
        b = cooked["blend"]["renderingDataBlock"]
        slot = self.slot_of(report, "moss-brick")
        for m, src, mode, n, channels in ((12, "moss.png", "RGBA", 7, 3), (28, "moss_n.png", "RGB", 5, 2), (20, "moss_r.png", "L", 4, 1)):
            t = next(b["textures"][p["parameterIndex"]]["texture"] for p in self.patches(b, SLOT, slot) if p["type"] == "Texture" and p["offset"] % SLOT == m)
            chain = (out / "blobs" / ("%x-%x-%x-%x.bin" % tuple(t["content"]["hash"][k] for k in ("size", "hash0", "hash1", "hash2")))).read_bytes()
            top = Image.frombytes(mode, (t["width"], t["height"]), chain, "bcn", (n,))
            ref = Image.open(mod / "textures" / src)
            se, count = 0.0, 0
            for y in range(0, t["height"], 3):
                for x in range(0, t["width"], 3):
                    a, r = top.getpixel((x, y)), ref.getpixel((x, y))
                    a = a if isinstance(a, tuple) else (a,)
                    r = r if isinstance(r, tuple) else (r,)
                    for c in range(channels):
                        se += (a[c] - r[c]) ** 2
                        count += 1
            psnr = 10 * math.log10(255 ** 2 / max(se / count, 1e-9))
            self.assertGreater(psnr, 30, (src, psnr))

    def test_a_surface_by_name_is_the_same_surface(self):
        report, cooked, *_ = self.cooked_lab()
        b = cooked["blend"]["renderingDataBlock"]
        d = bytes.fromhex(b["data"]["bytes"])
        a, bb = self.slot_of(report, "named-a"), self.slot_of(report, "named-b")
        self.assertEqual(d[a * SLOT:(a + 1) * SLOT], d[bb * SLOT:(bb + 1) * SLOT])
        hashes = lambda s: [b["textures"][p["parameterIndex"]]["texture"]["content"]["hash"] for p in self.patches(b, SLOT, s) if p["type"] == "Texture"]
        self.assertEqual(hashes(a), hashes(bb))
        self.assertAlmostEqual(struct.unpack_from("<f", d, a * SLOT + 0x180)[0], 0.3, places=5)

    def test_block_items_get_icons(self):
        """A new material without an icon of its own gets a block of its surface; a retexture or a block that names an icon doesn't."""
        report, cooked, raw, out, _ = self.cooked_lab()
        icon = {s["block"]: s["icon"] for s in report["surfaces"]}
        self.assertTrue(icon["moss-brick"] and icon["named-a"] and icon["moon-copy"] and icon["plain"], icon)
        self.assertFalse(icon["named-b"] or icon["snowy-logs"], icon)
        data = (out / "icons" / "surf-lab.named-a.png").read_bytes()
        self.assertEqual(data[:8], b"\x89PNG\r\n\x1a\n")
        w, h, depth, colour = struct.unpack(">IIBB", data[16:26])
        self.assertEqual((w, h, depth, colour), (256, 256, 8, 6))
        try:
            from PIL import Image
        except ImportError:
            return
        import io
        img = Image.open(io.BytesIO(data)).convert("RGBA")
        self.assertEqual(img.getpixel((2, 2))[3], 0)            # around the block: transparent
        self.assertEqual(img.getpixel((128, 128))[3], 255)      # the block
        top = img.getpixel((128, 60))                            # the top face: textures/top.png, unshaded
        self.assertTrue(all(abs(top[i] - (120, 160, 90)[i]) <= 6 for i in range(3)), top)

    def test_mistakes_fall_back_by_name(self):
        tex = {"textures/odd.png": png(96, 64, lambda x, y: (1, 2, 3)), "textures/ok.png": png(16, 16, lambda x, y: (9, 9, 9))}
        mod = write_mod(Path(tempfile.mkdtemp(dir=self.dir)), "surf-bad", {"buildingMaterials": {
            "missing": {"name": "Missing", "surface": {"side": {"albedo": "textures/none.png"}}},
            "odd-size": {"name": "Odd", "surface": {"side": {"albedo": "textures/odd.png"}}},
            "no-game": {"name": "No Game", "surface": {"side": {"game": "NotAMaterial"}}},
            "terrain": {"name": "Terrain", "surface": {"side": {"albedo": "textures/ok.png"}, "decoration": "Cell5"}},
            "fine": {"name": "Fine", "surface": {"side": {"albedo": "textures/ok.png"}}}}}, tex)
        report, cooked, _ = self.cook(mod, ok=(1,))
        text = "\n".join(report["errors"])
        self.assertIn("block buildingMaterial missing (mod surf-bad), surface.side.albedo: textures/none.png: no such file", text)
        self.assertIn("block buildingMaterial odd-size (mod surf-bad), surface.side.albedo: textures/odd.png: 96x64 is not a power of two", text)
        self.assertIn("block buildingMaterial no-game (mod surf-bad), surface.side.game: the game has no material \"NotAMaterial\"", text)
        self.assertIn("block buildingMaterial terrain (mod surf-bad), surface.decoration: \"Cell5\" is a terrain material", text)
        status = {s["block"]: s["status"] for s in report["surfaces"]}
        self.assertEqual(status, {"missing": "default", "odd-size": "default", "no-game": "default", "terrain": "default", "fine": "written"})
        self.assertEqual(cooked["blend"]["layerCount"], self.game["blend"]["layerCount"] + 5)

    def test_the_streamed_texture_limit_refuses_by_name(self):
        _, _, _, _, mod = self.cooked_lab()
        report, cooked, _ = self.cook(mod, extra=("--room", "3"), ok=(1,))
        text = "\n".join(report["errors"])
        self.assertIn("block buildingMaterial moss-brick (mod surf-lab), surface: refused: its", text)
        self.assertIn("the game has room for 3 more streamed textures", text)
        status = {s["block"]: s["status"] for s in report["surfaces"]}
        self.assertEqual(status["moss-brick"], "default")
        self.assertEqual(status["moon-copy"], "written")
        self.assertLessEqual(report["newStreamedTextures"], 3)

    def test_ids_past_the_room_are_refused_by_name(self):
        """Ids come from kiln.worldtables' own allocator; a surface for every placed material, and the one past the room is refused by name."""
        n = 128 - self.game["blend"]["layerCount"] + 1
        mats = {f"m{i:02d}": {"name": f"M{i}", "surface": {"side": {"game": "StoneRough"}}} for i in range(n)}
        mod = write_mod(Path(tempfile.mkdtemp(dir=self.dir)), "surf-many", {"buildingMaterials": mats}, {})
        report, cooked, _ = self.cook(mod, ok=(1,))
        refused = [e for e in report["errors"] if "refused" in e]
        self.assertEqual(len(refused), 1, refused)
        self.assertRegex(refused[0], r"^block buildingMaterial m\d\d \(mod surf-many\): refused: the game has room for \d+ new building materials")
        self.assertEqual(cooked["blend"]["layerCount"], 128)
        self.assertEqual(len(report["surfaces"]), n - 1)


if __name__ == "__main__":
    unittest.main()
