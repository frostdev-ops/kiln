# SPDX-License-Identifier: MIT
"""Structural checks on keen::RenderMaterialResource, as decoded by blob.Decoder.

The same rules run over the game's own materials and over what kiln-vfxcook writes, so "passes" means the output has the shape the game's data has. Every
rule holds for all 3,458 materials of client 26430625 (docs/re/vfx.md section 5). check_material(strict=True) adds what the builder promises on top.
"""
import struct

BLOCKS = {"Pbr": (0x1220f31f, 244), "TerraformingCursor": (0x6abb496a, 44), "SkyTexture": (0x6062cc91, 12)}
BLOCK_BYTES = {"BC1_RGB_unorm_block": 8, "BC1_RGB_srgb_block": 8, "BC4_unorm_block": 8, "BC5_unorm_block": 16, "BC6H_ufloat_block": 16, "BC7_unorm_block": 16, "BC7_srgb_block": 16}
CHAIN_CATEGORY = 1840640251
TAIL_CATEGORY = 1185060183
MAX_FEEDBACK_IMAGES = 8   # the loader aborts beyond it
MAX_MIP_RANGES = 16       # the texture system aborts beyond it

# keen::PbrRfxRenderMaterial's texture and feedback slots
PBR_TEXTURES = {0x04, 0x14, 0x18, 0x1c, 0x20, 0x24, 0x28, 0x2c, 0x30, 0x34, 0x38, 0x4c, 0x50, 0x70, 0x74, 0x78, 0x80, 0x84, 0x88, 0xa4, 0xb8, 0xbc}
PBR_FEEDBACKS = {0x08, 0x64, 0x68, 0xcc}
ALBEDO, NORMAL, EMISSIVE, PARAMS = 0x14, 0x1c, 0x24, 0x2c
FLAG = {"EnableDissolve": 1 << 0, "EnableHashedAlpha": 1 << 1, "EnableGradientMap": 1 << 2, "SampleAo": 1 << 3, "SampleOpacity": 1 << 4, "EnableContactShadows": 1 << 5,
        "EnableAnisotropicSpecular": 1 << 6, "FlipBackfaceNormals": 1 << 7, "MultiplyAlphaByVertexAlpha": 1 << 8, "DisableFrostShader": 1 << 9, "DisableSnowShader": 1 << 10,
        "DisableWetnessShader": 1 << 11, "EnableFrostShaderGlitter": 1 << 12, "LegacyTranslucency": 1 << 13, "FoliageTranslucency": 1 << 14}


def block_bytes(v):
    return bytes.fromhex(v["data"]["bytes"]) if isinstance(v["data"], dict) else bytes(v["data"])


def pbr_flags(v):
    return struct.unpack_from("<I", block_bytes(v), 0xf0)[0]


def mip_sizes(width, height, levels, block):
    return [((max(1, width >> k) + 3) // 4) * ((max(1, height >> k) + 3) // 4) * block for k in range(levels)]


def expected_ranges(sizes):
    """One range per level with more than 4 KiB left to the end of the blob, each running to the end."""
    total, out, cum = sum(sizes), [], 0
    for s in sizes:
        if total - cum > 4096:
            out.append({"start": cum, "count": total - cum})
        cum += s
    return out


def check_texture(t, guid, index, where):
    """Problems of one RenderStreamedTexture in a material."""
    bad = []
    if t["format"] not in BLOCK_BYTES:
        return [f"{where}: format {t['format']}"]
    side = max(t["width"], t["height"])
    if t["levelCount"] != side.bit_length():
        bad.append(f"{where}: {t['levelCount']} levels for {side}: not a full chain")
    sizes = mip_sizes(t["width"], t["height"], t["levelCount"], BLOCK_BYTES[t["format"]])
    content, tail = t["content"], t["mipTailContent"]
    if content["hash"]["size"] != sum(sizes):
        bad.append(f"{where}: the blob is {content['hash']['size']} bytes, {sum(sizes)} expected")
    ranges = t["mipChainContentRanges"]
    if ranges != expected_ranges(sizes):
        bad.append(f"{where}: mip ranges {ranges}")
    if len(ranges) > MAX_MIP_RANGES:
        bad.append(f"{where}: {len(ranges)} mip ranges: the game aborts above {MAX_MIP_RANGES}")
    if tail["hash"]["size"] != sum(sizes[len(ranges):]):
        bad.append(f"{where}: the mip tail is {tail['hash']['size']} bytes, {sum(sizes[len(ranges):])} expected")
    if not ranges and tail["hash"] != content["hash"]:
        bad.append(f"{where}: without ranges the tail is the chain")
    if content["category"]["value"] != CHAIN_CATEGORY or content["bundleId"] != int(guid[:8], 16) or content["sortKey"] != index:
        bad.append(f"{where}: content reference {content['category']['value']}, {content['bundleId']}, {content['sortKey']}")
    if tail["category"]["value"] != TAIL_CATEGORY or tail["bundleId"] != 0 or tail["sortKey"] != tail["hash"]["hash2"]:
        bad.append(f"{where}: mip tail reference {tail['category']['value']}, {tail['bundleId']}, {tail['sortKey']}")
    if not t["debugName"]:
        bad.append(f"{where}: no name")
    return bad


def check_material(guid, v, strict=False):
    """Problems of one decoded RenderMaterialResource, as a list of strings. Empty: it passes."""
    bad = []
    if v["effectId"] not in BLOCKS:
        return [f"effect {v['effectId']}: no block type"]
    signature, size = BLOCKS[v["effectId"]]
    data = block_bytes(v)
    if v["dataTypeSignature"]["value"] != signature:
        bad.append(f"signature {v['dataTypeSignature']['value']:#x} on effect {v['effectId']}: the loader refuses it")
    if len(data) != size:
        bad.append(f"its block is {len(data)} bytes, {size} expected")
    pbr = v["effectId"] == "Pbr"
    offsets = []
    for i, image in enumerate(v["images"]):
        o = image["dataOffset"]
        offsets.append(o)
        where = f"images[{i}] at +{o:#x}"
        if o + 4 > len(data) or o % 4:
            bad.append(f"{where}: outside the block")
            continue
        if pbr and o not in PBR_TEXTURES:
            bad.append(f"{where}: no texture slot of the block")
        if struct.unpack_from("<I", data, o)[0] != 0:
            bad.append(f"{where}: the slot holds a value: the loader writes a handle there")
        bad += check_texture(image["texture"], guid, i, where)
    if len(set(offsets)) != len(offsets):
        bad.append("two images in one slot")
    seen = set()
    for i, f in enumerate(v["textureFeedbacks"]):
        where = f"textureFeedbacks[{i}]"
        if pbr and f["dataOffset"] not in PBR_FEEDBACKS:
            bad.append(f"{where}: +{f['dataOffset']:#x} is no feedback slot of the block")
        if f["dataOffset"] + 4 > len(data) or struct.unpack_from("<I", data, f["dataOffset"])[0] != 0:
            bad.append(f"{where}: the slot holds a value")
        ix = f["renderMaterialImageIndices"]
        if len(ix) > MAX_FEEDBACK_IMAGES:
            bad.append(f"{where}: {len(ix)} images: the loader aborts above {MAX_FEEDBACK_IMAGES}")
        if any(x >= len(v["images"]) for x in ix):
            bad.append(f"{where}: names an image that isn't there")
        if seen & set(ix):
            bad.append(f"{where}: an image is in two lists")
        seen |= set(ix)
    if pbr:
        for slot, name in ((ALBEDO, "albedo"), (NORMAL, "normal"), (PARAMS, "params")):
            if slot not in offsets:
                bad.append(f"no {name} map: the G-buffer shader reads it unconditionally")
        if not v["textureFeedbacks"]:
            bad.append("no feedback: its textures would never stream past the mip tail")
        flags = pbr_flags(v)
        if ("CanDissolve" in v["flags"]) != bool(flags & FLAG["EnableDissolve"]):
            bad.append("CanDissolve and EnableDissolve disagree")
        if ("HashedAlpha" in v["flags"]) != bool(flags & FLAG["EnableHashedAlpha"]):
            bad.append("HashedAlpha and EnableHashedAlpha disagree")
        if "AlphaTested" in v["flags"] and not flags & FLAG["SampleOpacity"]:
            bad.append("AlphaTested without SampleOpacity")
        if bool(flags & FLAG["FlipBackfaceNormals"]) and "DoubleSided" not in v["flags"]:
            bad.append("FlipBackfaceNormals on a one-sided material")
        strength = struct.unpack_from("<f", data, 0xa0)[0]
        if ("Translucent" in v["flags"]) != (strength > 0):
            bad.append("Translucent and translucencyStrength disagree")
    if strict:
        bad += _check_kiln(v, data)
    return bad


def _check_kiln(v, data):
    """What kiln-vfxcook promises beyond the game's own rules."""
    bad = []
    if v["effectId"] != "Pbr":
        return ["not on the Pbr effect"]
    if struct.unpack_from("<I", data, 0)[0] != 0:
        bad.append("more than one layer")
    if len(v["textureFeedbacks"]) != 1 or v["textureFeedbacks"][0]["dataOffset"] != 0x64:
        bad.append("not the first layer's feedback")
    elif v["textureFeedbacks"][0]["renderMaterialImageIndices"] != list(range(len(v["images"]))):
        bad.append("the feedback doesn't list every image")
    for i in v["images"]:
        t = i["texture"]
        if t["width"] & (t["width"] - 1) or t["height"] & (t["height"] - 1):
            bad.append(f"+{i['dataOffset']:#x}: {t['width']}x{t['height']} is not a power of two")
    formats = {i["dataOffset"]: i["texture"]["format"] for i in v["images"]}
    if formats.get(ALBEDO) != "BC7_srgb_block":
        bad.append(f"albedo is {formats.get(ALBEDO)}")
    if formats.get(NORMAL) != "BC5_unorm_block":
        bad.append(f"normal is {formats.get(NORMAL)}")
    if formats.get(PARAMS) not in ("BC1_RGB_unorm_block", "BC7_unorm_block"):
        bad.append(f"params is {formats.get(PARAMS)}")
    if EMISSIVE in formats and [i["dataOffset"] for i in v["images"]][0] != EMISSIVE:
        bad.append("the emissive map isn't first")
    return bad
