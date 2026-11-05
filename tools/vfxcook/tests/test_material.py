# SPDX-License-Identifier: MIT
"""MaterialBlock: the writer, the builder and the budget, against tools/kfc.

    python3 -m unittest discover -s tools/vfxcook/tests -p 'test_material*' -v

No game files are needed. With the installed client (read only; skipped when absent): all of its materials read and rewrite byte for byte through the builder's own
code, the rules in kfc.material hold for every one of them, and what the budget assumes about this build holds. Nothing from the game is written anywhere but a temp folder.

What this can't show: how a material looks in the game. tools/vfxcook/tests/material_gpu.py runs the game's own G-buffer shader over them on a real GPU, offscreen.
"""
import json
import os
import struct
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

from test_vfxcook import DEC, ENC, GAME, HAS_GAME, NS, REPO, Base, Mod, cook, png, run, validate
from kfc import material as checks
from kfc.contenthash import content_hash

MATERIAL = "keen::RenderMaterialResource"
HOLE = "tex/hole.png"


def checker(w, h, a=255):
    """A PNG with two colours, the second alpha `a`: enough to tell channels and alpha apart."""
    import zlib

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
    rows = bytearray()
    for y in range(h):
        rows.append(0)
        for x in range(w):
            rows += bytes((255, 128, 32, 255)) if (x // 4 + y // 4) % 2 else bytes((16, 64, 200, a))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(bytes(rows))) + chunk(b"IEND", b"")


class MatMod(Mod):
    def __init__(self, root, mod_id, materials, vfx=None, **kw):
        doc = {"format": 1, "materials": materials}
        if vfx:
            doc["vfx"] = vfx
        super().__init__(root, mod_id, None, raw_doc=json.dumps(doc), **kw)


def guid_of(mod, name):
    return str(uuid.uuid5(NS, f"{mod}:material:{name}"))


def blocks_in(c):
    return {m["name"]: m for m in c.report["materials"]}


def decoded(c, mod, name):
    return c.decoded(guid_of(mod, name), MATERIAL)


def block_fields(v):
    d = bytes(v["data"]) if isinstance(v["data"], list) else bytes.fromhex(v["data"]["bytes"])
    f = lambda o: struct.unpack_from("<f", d, o)[0]
    u = lambda o: struct.unpack_from("<I", d, o)[0]
    return d, f, u


def bc1_endpoints(block):
    """The two colours of a BC1 block, 0..1 by channel."""
    c0, c1 = struct.unpack_from("<2H", block)
    def rgb(c):
        return ((c >> 11) / 31, ((c >> 5) & 63) / 63, (c & 31) / 31)
    return rgb(c0), rgb(c1)


def bc5_flat(block):
    """What a flat BC5 block's endpoints say, R and G, 0..255."""
    return block[0], block[8]


class Bytes(Base):
    def setUp(self):
        super().setUp()
        files = {"tex/a.png": checker(64, 64), "tex/n.png": checker(32, 16), "tex/e.png": checker(16, 16), "tex/big.png": checker(256, 256)}
        self.c = cook(MatMod(self.tmp, "m", {
            "plain": {"tint": "#ff0000"},
            "steel": {"kind": "metal", "albedo": "tex/big.png", "normal": "tex/n.png", "roughness": 0.3, "tint": "#bfe0ff"},
            "leaf": {"kind": "foliage", "albedo": "tex/a.png"},
            "rune": {"kind": "emissive", "albedo": "tex/a.png", "emissive": {"map": "tex/e.png", "color": "#7fd4ff", "strength": 8}},
        }, files=files), tmp=self.tmp)

    def test_round_trip_byte_identical(self):
        for r in self.c.resources_of(MATERIAL):
            raw = (self.c.out / r["file"]).read_bytes()
            self.assertEqual(ENC.encode(MATERIAL, DEC.decode(MATERIAL, raw)), raw, r["what"])

    def test_every_material_passes_the_rules(self):
        for name in blocks_in(self.c):
            self.assertEqual(checks.check_material(guid_of("m", name), decoded(self.c, "m", name), strict=True), [], name)

    def test_every_blob_a_material_names_is_served_under_its_real_hash(self):
        for name in blocks_in(self.c):
            for image in decoded(self.c, "m", name)["images"]:
                t = image["texture"]
                for ref in (t["content"], t["mipTailContent"]):
                    data = self.c.blob(ref["hash"])
                    h = ref["hash"]
                    self.assertEqual(tuple(content_hash(data)), (h["size"], h["hash0"], h["hash1"], h["hash2"]))

    def test_the_mip_tail_is_the_end_of_the_chain(self):
        t = decoded(self.c, "m", "steel")["images"][0]["texture"]
        self.assertEqual(t["width"], 256)
        chain, tail = self.c.blob(t["content"]["hash"]), self.c.blob(t["mipTailContent"]["hash"])
        self.assertEqual(chain[-len(tail):], tail)
        self.assertLess(len(tail), len(chain))

    def test_a_small_texture_has_no_ranges_and_one_blob(self):
        t = decoded(self.c, "m", "plain")["images"][0]["texture"]
        self.assertEqual((t["width"], t["height"], t["levelCount"]), (16, 16, 5))
        self.assertEqual(t["mipChainContentRanges"], [])
        self.assertEqual(t["content"]["hash"], t["mipTailContent"]["hash"])

    def test_same_input_same_bytes(self):
        again = cook(MatMod(self.tmp / "again", "m", {"plain": {"tint": "#ff0000"}}), tmp=self.tmp / "again")
        self.assertEqual(again.resource(guid_of("m", "plain"), MATERIAL), self.c.resource(guid_of("m", "plain"), MATERIAL))

    def test_the_manifest_lists_it_for_clients_only(self):
        entries = [e for e in self.c.manifest["entries"] if e["kind"] == "resource"]
        self.assertTrue(all(e["type"] == MATERIAL and e["side"] == "client" and e["mode"] == "new" for e in entries))
        self.assertEqual(len(entries), 4)

    def test_a_constant_map_is_shared_by_all_who_use_it(self):
        def hash_of(name, slot):
            return next(i["texture"]["content"]["hash"] for i in decoded(self.c, "m", name)["images"] if i["dataOffset"] == slot)
        self.assertEqual(hash_of("plain", checks.NORMAL), hash_of("rune", checks.NORMAL))
        # one streamed texture for them in the budget too
        hashes = {str(i["texture"]["content"]["hash"]) for n in blocks_in(self.c) for i in decoded(self.c, "m", n)["images"]}
        self.assertEqual(self.c.report["budget"]["streamedTextures"]["mods"], len(hashes))


class Kinds(Base):
    PIPELINES = {
        "opaque": ("Model_Visibility_GpuCulling2_Instanced", "Model_GBuffer_GpuCulling2_Instanced", "Model_Shadow_GpuCulling2_Instanced"),
        "metal": ("Model_Visibility_GpuCulling2_Instanced", "Model_GBuffer_GpuCulling2_Instanced", "Model_Shadow_GpuCulling2_Instanced"),
        "cutout": ("Model_Visibility_Discard_GpuCulling2_Instanced", "Model_GBuffer_GpuCulling2_Instanced", "Model_Shadow_Discard_GpuCulling2_Instanced"),
        "foliage": ("Model_Visibility_Discard_DoubleSided_GpuCulling2_Instanced", "Model_GBuffer_DoubleSided_GpuCulling2_Instanced", "Model_Shadow_Discard_DoubleSided_GpuCulling2_Instanced"),
        "cloth": ("Model_Visibility_DoubleSided_GpuCulling2_Instanced", "Model_GBuffer_DoubleSided_GpuCulling2_Instanced", "Model_Shadow_DoubleSided_GpuCulling2_Instanced"),
        "emissive": ("Model_Visibility_GpuCulling2_Instanced", "Model_GBuffer_GpuCulling2_Instanced", "Model_Shadow_GpuCulling2_Instanced"),
        "dithered": ("Model_Visibility_HashedAlpha_GpuCulling2_Instanced", "Model_GBuffer_GpuCulling2_Instanced", "Model_Shadow_HashedAlpha_GpuCulling2_Instanced"),
    }

    def test_each_kind_goes_to_its_pipelines(self):
        c = cook(MatMod(self.tmp, "m", {k: {"kind": k} for k in self.PIPELINES}), tmp=self.tmp)
        got = blocks_in(c)
        for kind, want in self.PIPELINES.items():
            self.assertEqual(tuple(got[kind]["pipelines"]), want, kind)
            self.assertEqual(checks.check_material(guid_of("m", kind), decoded(c, "m", kind), strict=True), [], kind)

    def test_kind_defaults_and_an_explicit_field_wins(self):
        c = cook(MatMod(self.tmp, "m", {"a": {"kind": "foliage"}, "b": {"kind": "foliage", "doubleSided": False, "alphaTest": False, "translucent": 0},
                                        "m": {"kind": "metal"}, "r": {"kind": "metal", "metallic": 0, "roughness": 1}}), tmp=self.tmp)
        self.assertEqual(sorted(decoded(c, "m", "a")["flags"]), ["AlphaTested", "DoubleSided", "Foliage", "Translucent"])
        self.assertEqual(decoded(c, "m", "b")["flags"], ["Foliage"])

        def params(name):
            v = decoded(c, "m", name)
            t = next(i["texture"] for i in v["images"] if i["dataOffset"] == checks.PARAMS)
            return bc1_endpoints(c.blob(t["content"]["hash"])[:8])[0]
        self.assertAlmostEqual(params("m")[0], 1.0, 1)
        self.assertAlmostEqual(params("m")[1], 0.35, 1)
        self.assertAlmostEqual(params("r")[0], 0.0, 1)
        self.assertAlmostEqual(params("r")[1], 1.0, 1)

    def test_transparent_says_where_it_comes_from(self):
        c = cook(MatMod(self.tmp, "m", {"glass": {"kind": "transparent"}}), tmp=self.tmp, ok=(1,))
        self.assertIn("m: blocks/b.json: materials.glass.kind", c.report["errors"][0])
        self.assertIn("ShaderBlock", c.report["errors"][0])


class Fields(Base):
    def test_the_block(self):
        c = cook(MatMod(self.tmp, "m", {"x": {"uvScale": [2, 3], "reflectance": 0.25, "translucent": 0.5, "vertexColor": True, "shadows": False, "frost": False, "snow": False, "wetness": False,
                                              "contactShadows": False, "doubleSided": True, "emissive": {"color": "#ffffff", "strength": 8, "exposure": 0.5},
                                              "dissolve": {"color": "#ff0000", "width": 0.1, "noiseTiling": 2, "noiseBlend": 0.25}}}), tmp=self.tmp)
        v = decoded(c, "m", "x")
        d, f, u = block_fields(v)
        self.assertEqual(len(d), 244)
        self.assertEqual(v["dataTypeSignature"]["value"], 0x1220f31f)
        self.assertEqual(v["effectId"], "Pbr")
        self.assertEqual((f(0x3c), f(0x40)), (2, 3))
        self.assertAlmostEqual(f(0x5c), 0.25)
        self.assertEqual(f(0x8c), 1)
        self.assertEqual((u(0x94), u(0x98), u(0x9c)), (255, 0, 255))
        self.assertEqual(f(0xa0), 0.5)
        self.assertEqual((f(0xa8), f(0xac)), (8, 0.5))
        self.assertEqual((f(0xd0), f(0xd4), f(0xd8)), (1, 0, 0))
        self.assertAlmostEqual(f(0xdc), 10, 4)
        self.assertEqual(f(0xe0), 1)
        self.assertEqual(u(0xe4), 2)
        self.assertEqual((f(0xe8), f(0xec)), (2, 0.25))
        flags = u(0xf0)
        for name in ("EnableDissolve", "FlipBackfaceNormals", "DisableFrostShader", "DisableSnowShader", "DisableWetnessShader", "FoliageTranslucency"):
            self.assertTrue(flags & checks.FLAG[name], name)
        for name in ("EnableContactShadows", "SampleOpacity", "EnableHashedAlpha"):
            self.assertFalse(flags & checks.FLAG[name], name)
        self.assertEqual(sorted(v["flags"]), ["CanDissolve", "DisableShadows", "DoubleSided", "Foliage", "Translucent"])

    def test_defaults_are_the_games_usual_values(self):
        c = cook(MatMod(self.tmp, "m", {"x": {}}), tmp=self.tmp)
        v = decoded(c, "m", "x")
        d, f, u = block_fields(v)
        self.assertEqual((f(0x3c), f(0x40), f(0x44), f(0x48)), (1, 1, 0, 0))
        self.assertAlmostEqual(f(0x5c), 0.5028854608535767)
        self.assertEqual((u(0x94), u(0x98), u(0x9c)), (255, 255, 255))
        self.assertEqual((f(0xa8), f(0xac)), (1, 0.75))
        self.assertEqual(u(0xf0), checks.FLAG["EnableContactShadows"])
        self.assertEqual(v["flags"], ["Foliage"])
        self.assertEqual([i["dataOffset"] for i in v["images"]], [checks.ALBEDO, checks.NORMAL, checks.PARAMS])
        self.assertEqual(v["textureFeedbacks"], [{"dataOffset": 0x64, "renderMaterialImageIndices": [0, 1, 2]}])

    def test_the_formats_are_the_games(self):
        c = cook(MatMod(self.tmp, "m", {"x": {"emissive": "#ffffff"}}), tmp=self.tmp)
        got = {i["dataOffset"]: i["texture"]["format"] for i in decoded(c, "m", "x")["images"]}
        self.assertEqual(got[checks.ALBEDO], "BC7_srgb_block")
        self.assertEqual(got[checks.NORMAL], "BC5_unorm_block")
        self.assertEqual(got[checks.PARAMS], "BC1_RGB_unorm_block")
        self.assertEqual(got[checks.EMISSIVE], "BC7_srgb_block")
        self.assertEqual([i["dataOffset"] for i in decoded(c, "m", "x")["images"]][0], checks.EMISSIVE)

    def test_a_flat_normal_is_flat(self):
        c = cook(MatMod(self.tmp, "m", {"x": {}}), tmp=self.tmp)
        t = next(i["texture"] for i in decoded(c, "m", "x")["images"] if i["dataOffset"] == checks.NORMAL)
        r, g = bc5_flat(c.blob(t["content"]["hash"]))
        self.assertAlmostEqual(r, 128, delta=1)
        self.assertAlmostEqual(g, 128, delta=1)

    def test_reflectance_from_params_takes_a_format_with_alpha(self):
        c = cook(MatMod(self.tmp, "m", {"x": {"params": "p.png", "reflectance": "params"}}, files={"p.png": png(8, 8)}), tmp=self.tmp)
        v = decoded(c, "m", "x")
        _, f, u = block_fields(v)
        self.assertEqual(f(0x5c), -1)
        self.assertTrue(u(0xf0) & checks.FLAG["SampleAo"])
        self.assertEqual(next(i["texture"]["format"] for i in v["images"] if i["dataOffset"] == checks.PARAMS), "BC7_unorm_block")

    def test_gltf_channels_become_the_games(self):
        def solid(r, g, b):
            return png(4, 4, (r, g, b, 255))
        c = cook(MatMod(self.tmp, "m", {"x": {"metallicRoughness": "mr.png", "occlusion": "ao.png", "roughness": 0.5}},
                        files={"mr.png": solid(10, 200, 240), "ao.png": solid(120, 0, 0)}), tmp=self.tmp)
        v = decoded(c, "m", "x")
        t = next(i["texture"] for i in v["images"] if i["dataOffset"] == checks.PARAMS)
        r, g, b = bc1_endpoints(c.blob(t["content"]["hash"])[:8])[0]
        self.assertAlmostEqual(r, 240 / 255, delta=0.05)      # metallic: B
        self.assertAlmostEqual(g, 100 / 255, delta=0.05)      # roughness: G, by the factor
        self.assertAlmostEqual(b, 120 / 255, delta=0.05)      # occlusion: R of its own map
        self.assertTrue(block_fields(v)[2](0xf0) & checks.FLAG["SampleAo"])

    def test_maps_of_two_sizes_are_brought_to_the_larger(self):
        c = cook(MatMod(self.tmp, "m", {"x": {"metallicRoughness": "mr.png", "occlusion": "ao.png"}}, files={"mr.png": png(64, 64), "ao.png": png(16, 16)}), tmp=self.tmp)
        t = next(i["texture"] for i in decoded(c, "m", "x")["images"] if i["dataOffset"] == checks.PARAMS)
        self.assertEqual((t["width"], t["height"]), (64, 64))

    def test_a_guid_follows_the_block_rule_and_was_keeps_it(self):
        c = cook(MatMod(self.tmp, "m", {"new_name": {"was": "old_name"}}), tmp=self.tmp)
        self.assertEqual(blocks_in(c)["new_name"]["guid"], guid_of("m", "old_name"))
        self.assertEqual(run("id", "m", "material", "old_name").stdout.strip(), guid_of("m", "old_name"))


class Particles(Base):
    def test_a_particle_takes_the_materials_albedo_tint_and_light(self):
        c = cook(MatMod(self.tmp, "m", {"steel": {"kind": "metal", "albedo": "a.png", "tint": "#808080"}},
                        vfx={"fx": {"particles": [{"burst": 1, "material": "steel"}]}}, files={"a.png": png(8, 8)}), tmp=self.tmp)
        names = [r["what"] for r in c.resources_of("keen::RenderTextureResource")]
        self.assertTrue(any("a.png" in n for n in names), names)
        self.assertEqual(c.report["errors"], [])

    def test_a_particle_that_says_its_own_keeps_it(self):
        c = cook(MatMod(self.tmp, "m", {"steel": {"albedo": "a.png"}}, vfx={"fx": {"particles": [{"burst": 1, "material": "steel", "texture": "b.png", "lit": False}]}},
                        files={"a.png": png(8, 8), "b.png": png(16, 16)}), tmp=self.tmp)
        names = [r["what"] for r in c.resources_of("keen::RenderTextureResource")]
        self.assertTrue(any("b.png" in n for n in names) and not any("a.png" in n for n in names), names)

    def test_a_material_that_isnt_there(self):
        c = cook(MatMod(self.tmp, "m", {}, vfx={"fx": {"particles": [{"burst": 1, "material": "nope"}]}}), tmp=self.tmp, ok=(1,))
        self.assertIn("vfx.fx.particles[0].material: \"nope\" is no material of this mod", c.report["errors"][0])
        self.assertEqual(c.report["blocks"], [])


class Mistakes(Base):
    runs = 0

    def bad(self, block, want, files=None):
        Mistakes.runs += 1
        folder = self.tmp / f"run{Mistakes.runs}"
        c = cook(MatMod(folder, "m", {"x": block}, files=files), tmp=folder, ok=(1,))
        self.assertTrue(any(want in e for e in c.report["errors"]), c.report["errors"])
        self.assertEqual(c.report["materials"], [])

    def test_named_fields(self):
        for block, want in (
            ({"typo": 1}, "materials.x.typo: unknown field"),
            ({"kind": "plastic"}, "materials.x.kind: expected opaque"),
            ({"albedo": 3}, "materials.x.albedo: expected a path"),
            ({"tint": "red"}, "materials.x.tint: expected a colour"),
            ({"roughness": 2}, "materials.x.roughness: 2 is more than 1"),
            ({"metallic": -1}, "materials.x.metallic: -1 is less than 0"),
            ({"normal": {"strength": 1}}, "materials.x.normal.map: expected a path"),
            ({"params": "a.png", "occlusion": "b.png"}, "materials.x.params: holds metallic"),
            ({"reflectance": "params"}, "materials.x.reflectance: \"params\" reads the alpha"),
            ({"alphaTest": 0}, "materials.x.alphaTest: must be above 0"),
            ({"alphaTest": "yes"}, "materials.x.alphaTest: expected a number"),
            ({"dissolve": {"width": 0}}, "materials.x.dissolve.width: must be above 0"),
            ({"dissolve": "yes"}, "materials.x.dissolve: expected true"),
            ({"uvScale": "big"}, "materials.x.uvScale: expected a number"),
            ({"emissive": 3}, "materials.x.emissive: expected a PNG"),
            ({"shader": "my_shader"}, "materials.x.shader: surface shaders aren't built yet"),
            ({"was": ""}, "materials.x.was: expected the block's old name"),
        ):
            with self.subTest(want):
                self.bad(block, want)

    def test_textures_by_field(self):
        for block, want, files in (
            ({"albedo": "no.png"}, "materials.x.albedo: no.png: can't read it", None),
            ({"normal": "../up.png"}, "materials.x.normal: ../up.png: must be relative", None),
            ({"albedo": "a.png"}, "materials.x.albedo: a.png: not a PNG", {"a.png": b"nope"}),
            ({"albedo": "a.png"}, "materials.x.albedo: a.png: 3x4 is not a power of two", {"a.png": png(3, 4)}),
            ({"emissive": "a.png"}, "materials.x.emissive: a.png: 2x2 is outside 4..4096", {"a.png": png(2, 2)}),
            ({"params": "a.png"}, "materials.x.params: a.png: 8192x8 is outside 4..4096", {"a.png": png(8192, 8)}),
        ):
            with self.subTest(want):
                self.bad(block, want, files)

    def test_one_bad_block_doesnt_take_the_others(self):
        c = cook(MatMod(self.tmp, "m", {"bad": {"roughness": 9}, "good": {}}), tmp=self.tmp, ok=(1,))
        self.assertEqual(list(blocks_in(c)), ["good"])

    def test_two_blocks_with_one_id(self):
        c = cook(MatMod(self.tmp, "m", {"a": {}, "b": {"was": "a"}}), tmp=self.tmp, ok=(1,))
        self.assertIn("\"was\" names the id of another block", c.report["errors"][0])

    def test_the_manifest_rule(self):
        c = cook(MatMod(self.tmp, "m", {"a": {}}, clients="kiln"), tmp=self.tmp, ok=(1,))
        self.assertIn("needs \"clients\": \"mod\"", c.report["refusedMods"][0])
        self.assertEqual(c.report["materials"], [])

    def test_a_server_mod_is_skipped(self):
        c = cook(MatMod(self.tmp, "m", {"a": {}}, side="server", clients="none"), tmp=self.tmp)
        self.assertEqual(c.report["materials"], [])
        self.assertIn("side \"server\"", c.stderr)


class Budget(Base):
    def test_the_room_is_what_the_game_leaves(self):
        b = json.loads(run("budget").stdout)
        self.assertEqual(b["materials"], {"game": 3403, "capacity": 4096, "room": 693})
        self.assertEqual(b["streamedTextures"], {"game": 13930, "capacity": 16383, "room": 2453})

    def test_the_last_material_fits_and_the_next_does_not(self):
        one = {"a": {"tint": "#112233"}}
        c = cook(MatMod(self.tmp / "ok", "m", one), extra=("--counts", "materials=4095"), tmp=self.tmp / "ok")
        self.assertEqual(c.report["budget"]["materials"]["mods"], 1)
        c = cook(MatMod(self.tmp / "no", "m", one), extra=("--counts", "materials=4096"), tmp=self.tmp / "no", ok=(1,))
        self.assertIn("materials: needs 1, 0 left", c.report["refusedMods"][0])
        self.assertEqual(c.report["materials"], [])
        self.assertEqual(c.manifest["entries"], [])

    def test_the_last_textures_fit_and_the_next_do_not(self):
        one = {"a": {"tint": "#112233"}}  # albedo, normal, params
        c = cook(MatMod(self.tmp / "ok", "m", one), extra=("--counts", "streamedTextures=16380"), tmp=self.tmp / "ok")
        self.assertEqual(c.report["budget"]["streamedTextures"]["mods"], 3)
        c = cook(MatMod(self.tmp / "no", "m", one), extra=("--counts", "streamedTextures=16381"), tmp=self.tmp / "no", ok=(1,))
        self.assertIn("streamed textures: needs 3, 2 left", c.report["refusedMods"][0])

    def test_the_budget_is_shared_by_all_mods_in_load_order(self):
        a = MatMod(self.tmp, "a", {"x": {"tint": "#010203"}})
        b = MatMod(self.tmp, "b", {"y": {"tint": "#040506"}})
        c = cook(a, b, extra=("--counts", "materials=4095"), tmp=self.tmp, ok=(1,))
        self.assertEqual([m["mod"] for m in c.report["materials"]], ["a"])
        self.assertTrue(c.report["refusedMods"][0].startswith("b:"))

    def test_a_texture_two_mods_share_counts_once(self):
        a = MatMod(self.tmp, "a", {"x": {"tint": "#010203"}})
        b = MatMod(self.tmp, "b", {"y": {"tint": "#010203"}})
        c = cook(a, b, tmp=self.tmp)
        self.assertEqual(c.report["budget"]["streamedTextures"]["mods"], 3)
        self.assertEqual(c.report["budget"]["materials"]["mods"], 2)

    def test_too_many_vfx_also_refuses_the_mods_materials(self):
        c = cook(MatMod(self.tmp, "m", {"x": {}}, vfx={"v": {"light": {}}}), extra=("--counts", "vfx=2048"), tmp=self.tmp, ok=(1,))
        self.assertEqual(c.report["materials"], [])
        self.assertIn("VFX: needs 1, 0 left", c.report["refusedMods"][0])


class Writer(Base):
    def _roundtrip(self, records):
        f = self.tmp / "records.bin"
        with open(f, "wb") as out:
            for r in records:
                out.write(struct.pack("<I", len(r)))
                out.write(r)
        p = run("material-roundtrip", f, ok=(0, 1))
        return json.loads(p.stdout), p.returncode

    def test_what_it_builds_it_reads_back(self):
        c = cook(MatMod(self.tmp, "m", {"x": {"emissive": "#fff"}, "y": {"kind": "dithered"}}), tmp=self.tmp)
        recs = [(c.out / r["file"]).read_bytes() for r in c.resources_of(MATERIAL)]
        got, rc = self._roundtrip(recs)
        self.assertEqual((rc, got["records"], got["identical"]), (0, 2, 2))

    def test_a_cut_resource_is_refused_not_read_past_its_end(self):
        c = cook(MatMod(self.tmp, "m", {"x": {}}), tmp=self.tmp)
        raw = (c.out / c.resources_of(MATERIAL)[0]["file"]).read_bytes()
        got, rc = self._roundtrip([raw[:39], raw[:60]])
        self.assertEqual(rc, 1)
        self.assertEqual(got["identical"], 0)
        self.assertEqual(len(got["different"]), 2)


class Schema(Base):
    @classmethod
    def setUpClass(cls):
        cls.schema = json.loads((REPO / "schema" / "kiln-vfx.schema.json").read_text())

    def check(self, block):
        return validate(block, self.schema["$defs"]["material"], self.schema)

    def test_the_same_fields_as_the_builder(self):
        c = cook(MatMod(self.tmp, "m", {"bad": {"zzz": 1}}), tmp=self.tmp, ok=(1,))
        line = next(e for e in c.report["errors"] if "unknown field; there are " in e)
        known = set(line.split("unknown field; there are ")[1].split(", "))
        self.assertEqual(known | {"comment"}, set(self.schema["$defs"]["material"]["properties"]))

    def test_the_same_kinds_as_the_builder(self):
        for kind in self.schema["$defs"]["material"]["properties"]["kind"]["enum"]:
            cook(MatMod(self.tmp / kind, "m", {"x": {"kind": kind}}), tmp=self.tmp / kind)

    def test_what_the_schema_takes_the_builder_takes(self):
        block = {"kind": "metal", "albedo": "t.png", "tint": "#fff", "normal": {"map": "t.png", "strength": 2, "yDown": True, "comment": "x"}, "metallicRoughness": "t.png", "occlusion": "t.png",
                 "roughness": 0.5, "metallic": 0.5, "reflectance": 0.5, "emissive": {"map": "t.png", "color": "#000", "strength": 2, "exposure": 0.5, "comment": 1}, "alphaTest": 0.5, "doubleSided": True,
                 "shadows": False, "contactShadows": False, "frost": False, "snow": False, "wetness": False, "vertexColor": True, "translucent": 0.5,
                 "dissolve": {"color": "#fff", "width": 0.1, "noiseTiling": 1, "noiseBlend": 0.5, "comment": 1}, "uvScale": [1, 2], "was": "old", "comment": "y"}
        self.assertEqual(self.check(block), [])
        c = cook(MatMod(self.tmp, "m", {"x": block}, files={"t.png": png(8, 8)}), tmp=self.tmp)
        self.assertEqual(list(blocks_in(c)), ["x"])

    def test_mistakes_the_schema_catches_too(self):
        for block in ({"typo": 1}, {"kind": "plastic"}, {"albedo": "a.tga"}, {"tint": "red"}, {"roughness": 2}, {"emissive": 3}, {"alphaTest": 0}, {"dissolve": {"width": 0}}, {"uvScale": "big"}):
            with self.subTest(block):
                self.assertTrue(self.check(block))


@unittest.skipUnless(HAS_GAME, "the game isn't installed")
class WithTheGame(Base):
    """Read-only against the installed client. Nothing from it goes into the repo."""

    @classmethod
    def setUpClass(cls):
        os.environ.setdefault("KILN_OUT", str(Path(tempfile.gettempdir()) / "kiln-vfxcook-test"))
        from kfc.archive import Kfc
        from kfc.blob import Decoder
        from kfc.blobwrite import Encoder
        from kfc.refl import TypeDb
        cls.kfc = Kfc(GAME)
        cls.types = TypeDb.from_exe(GAME / "enshrouded.exe")
        cls.dec = Decoder(cls.types, max_array=1 << 26)
        cls.enc = Encoder(cls.types)
        cls.th = cls.types.get(MATERIAL)["hash"]
        cls.keys = list(cls.kfc.resources_of_type(cls.th))

    def test_every_game_material_rewrites_byte_identical(self):
        f = self.tmp / "records.bin"
        with open(f, "wb") as out:
            for k in self.keys:
                raw = self.kfc.read(k)
                out.write(struct.pack("<I", len(raw)))
                out.write(raw)
        got = json.loads(run("material-roundtrip", f).stdout)
        self.assertEqual(got, {"records": 3458, "identical": 3458, "different": []})

    def test_the_rules_hold_for_every_game_material(self):
        bad = {}
        for k in self.keys:
            problems = checks.check_material(k.guid, self.dec.decode(self.th, self.kfc.read(k)))
            if problems:
                bad[k.guid] = problems
        self.assertEqual(bad, {})

    def test_what_is_built_decodes_with_the_games_reflection(self):
        c = cook(MatMod(self.tmp, "m", {"x": {"emissive": "#fff", "dissolve": True}}), tmp=self.tmp)
        raw = c.resource(guid_of("m", "x"), MATERIAL)
        v = self.dec.decode(self.th, raw)
        self.assertEqual(self.enc.encode(self.th, v), raw)
        self.assertEqual(checks.check_material(guid_of("m", "x"), v, strict=True), [])
        # the block as the game's type
        block = self.dec.decode(self.types.get("keen::PbrRfxRenderMaterial")["hash"], checks.block_bytes(v))
        self.assertEqual(block["emissiveMapShaderData"][0]["exposureCorrectionFactor"], 0.75)
        self.assertEqual(block["texCoordScaling"][0], {"x": 1.0, "y": 1.0})

    def test_the_layouts_match_this_build(self):
        d = json.loads(run("fingerprint", "--exe", GAME / "enshrouded.exe").stdout)
        self.assertTrue(d["materialLayoutsOk"], d.get("materialDifference"))

    def test_the_budget_counts_match_this_build(self):
        # Distinct textures of the 8 types the renderer lists at boot. GameCounts holds this: it can't be read at boot without decompressing resources.
        seen = set()

        def walk(v):
            if isinstance(v, dict):
                if "mipTailContent" in v and "content" in v:
                    h = v["content"]["hash"]
                    if h["size"]:
                        seen.add((h["size"], h["hash0"], h["hash1"], h["hash2"]))
                    return
                for x in v.values():
                    walk(x)
            elif isinstance(v, list):
                for x in v:
                    walk(x)
        for name in ("BuildingMaterialBlendingResource", "CardClusterResource", "RenderDecal", "DetailScatterResource", "GiVoxelBuildingMaterialResource", "RenderMaterialResource",
                     "SimpleWorldMaterialResource", "WorldMaterialBlending2Resource"):
            ty = self.types.get("keen::" + name)
            for k in self.kfc.resources_of_type(ty["hash"]):
                walk(self.dec.decode(ty["hash"], self.kfc.read(k)))
        b = json.loads(run("budget", "--kfc", GAME / "enshrouded.kfc").stdout)
        self.assertEqual(b["streamedTextures"]["game"], len(seen))
        pbr = sum(1 for k in self.keys if self.kfc.read(k)[0] == 1)
        self.assertEqual(b["materials"]["game"], pbr)

    def test_each_kinds_pipelines_exist(self):
        th = self.types.get("keen::GpcResource")["hash"]
        names = set()
        for k in self.kfc.resources_of_type(th):
            v = self.dec.decode(th, self.kfc.read(k))
            if v["debugName"] == "renderer/renderer.gpc":
                names = {p["name"] for p in v["rasterPipelines"]}
        for kind, want in Kinds.PIPELINES.items():
            for p in want:
                self.assertIn(p, names, kind)


if __name__ == "__main__":
    unittest.main()
