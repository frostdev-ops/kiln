# SPDX-License-Identifier: MIT
"""Structural checks on VFX resources, as decoded by blob.Decoder.

The same rules run over the game's own resources and over what kiln-vfxcook writes, so "passes" means the output has the shape the
game's data has. Every check here holds for all 1,575 VfxResources, 638 VfxMeshResources and the 145 textures vanilla VFX name on client
26430625 (docs/re/vfx.md section 2). check_vfx(strict=True) adds what the builder promises on top: the starter modules' block layout
and the limits the modules can't enforce.
"""
import re
import struct

PIPELINE_CATEGORY = 2603049049
MESH_CATEGORY = 2543730610
TEXTURE_CATEGORY = 4010512636
NO_PARENT = 0xffffffff
MAX_RESOURCE_DATA = 65536
MAX_PARTICLES = 4096
GUID_TEXT = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")

# (array, words each patch writes): meshes, models, decals and sounds take one u32, images a u32 pair, curves five words
PATCH_BYTES = {"meshes": 4, "models": 4, "decals": 4, "images": 8, "lightFilters": 4, "sounds": 4, "curves": 20}
BC_BYTES = {"BC7_unorm_block": 16, "BC7_srgb_block": 16, "BC5_unorm_block": 16}


def blob_bytes(v):
    """A BlobArray<u8> as bytes: the decoder gives a list when short and {"bytes": hex} when long."""
    if isinstance(v, dict):
        return bytes.fromhex(v["bytes"])
    return bytes(v)


def resource_data(v):
    return blob_bytes(v["gpuResourceData"])


def check_vfx(v, strict=False):
    """Problems of one decoded VfxResource, as a list of strings. Empty: it passes."""
    bad = []
    data = resource_data(v)
    n = len(data)
    entries = v["particles"]
    if n % 4:
        bad.append(f"gpuResourceData is {n} bytes, not a multiple of 4")
    if n >= MAX_RESOURCE_DATA:
        bad.append(f"gpuResourceData is {n} bytes: Range16 can't reach it")
    if len(v["stateInfos"]) != len(entries):
        bad.append("stateInfos and particles differ in length")
    if sum(1 for p in entries if p["child"]) != v["emitterCount"]:
        bad.append("emitterCount is not the number of entries with a child")
    if v["useNewVm"]:
        bad.append("useNewVm is set")
    for i, p in enumerate(entries):
        where = f"particles[{i}]"
        if p["depth"] not in (0, 1):
            bad.append(f"{where}: depth {p['depth']}")
        if p["depth"] == 0 and p["parent"] != NO_PARENT:
            bad.append(f"{where}: a root with a parent")
        if p["depth"] == 1:
            if p["parent"] >= len(entries) or entries[p["parent"]]["child"] != i or entries[p["parent"]]["depth"] != 0:
                bad.append(f"{where}: its parent does not point back")
            if p["child"]:
                bad.append(f"{where}: a child with a child")
        if p["child"]:
            if p["child"] >= len(entries) or entries[p["child"]]["parent"] != i or p["child"] <= i:
                bad.append(f"{where}: its child does not point back")
            if p["depth"] != 0:
                bad.append(f"{where}: only roots have children")
        if p["maxCount"] < 1:
            bad.append(f"{where}: maxCount 0")
        ss = p["stateSizeInBytes"]
        if ss % 4 or ss < 20:
            bad.append(f"{where}: stateSizeInBytes {ss}")
        if i < len(v["stateInfos"]):
            si = v["stateInfos"][i]
            if si["sizeInBytes"] != ss:
                bad.append(f"{where}: stateInfos says {si['sizeInBytes']}, the entry {ss}")
            prev = 20
            for m in si["members"]:
                if m["offsetInBytes"] < prev or m["offsetInBytes"] % 4 or m["offsetInBytes"] + 4 * m["componentCount"] > ss:
                    bad.append(f"{where}: state member {m['name']} is out of place")
                prev = m["offsetInBytes"]
        for name in ("initializeStateGpuResourceDataRange", "evaluateParametersGpuResourceDataRange"):
            r = p[name]
            if r["start"] % 4 or r["count"] % 4 or r["start"] + r["count"] > n:
                bad.append(f"{where}: {name} {r} doesn't fit {n} bytes")
        s = p["strippedCode"]
        if s["category"]["value"] != PIPELINE_CATEGORY or s["bundleId"] != 1 or s["sortKey"] != i:
            bad.append(f"{where}: strippedCode reference {s['category']['value']}, {s['bundleId']}, {s['sortKey']}")
        h = s["hash"]
        if h["size"] == 0 or (h["hash0"] == 0 and h["hash1"] == 0 and h["hash2"] == 0):
            bad.append(f"{where}: strippedCode has no hash: the game aborts on it")
        if not GUID_TEXT.fullmatch(p["debugName"]):
            bad.append(f"{where}: debugName {p['debugName']!r} is not a GUID")
        if p["initialState"] and len(blob_bytes(p["initialState"])) > ss - 20:
            bad.append(f"{where}: initialState is larger than the state")
    for arr, size in PATCH_BYTES.items():
        prev = -1
        for k, pp in enumerate(v[arr]):
            r = pp["patchPoint"]["resourceDataRange"]
            if r["count"] != size:
                bad.append(f"{arr}[{k}]: writes {r['count']} bytes, not {size}")
            if r["start"] % 4 or r["start"] + r["count"] > n:
                bad.append(f"{arr}[{k}]: {r} doesn't fit {n} bytes")
            if r["start"] < prev:
                bad.append(f"{arr}[{k}]: out of order")
            prev = r["start"]
    if strict:
        bad += _check_kiln(v)
    return bad


def _check_kiln(v):
    """What kiln-vfxcook promises beyond the game's own rules."""
    bad = []
    data = resource_data(v)
    n = len(data)
    entries = v["particles"]
    versions = set()
    ranges = []
    for i, p in enumerate(entries):
        where = f"particles[{i}]"
        if p["maxCount"] > MAX_PARTICLES:
            bad.append(f"{where}: maxCount {p['maxCount']} is over {MAX_PARTICLES}")
        if p["depth"] == 0 and p["maxCount"] != 1:
            bad.append(f"{where}: a root gets one state, whatever maxCount says")
        if p["code"]["hash"]["size"] or p["code"]["category"]["value"]:
            bad.append(f"{where}: code is not empty")
        if p["initialState"]:
            bad.append(f"{where}: initialState is not empty")
        e, ini = p["evaluateParametersGpuResourceDataRange"], p["initializeStateGpuResourceDataRange"]
        if ini["count"] != 0 or ini["start"] != e["start"]:
            bad.append(f"{where}: its initialise range should be empty")
        ranges.append((e["start"], e["count"]))
        if p["depth"] == 0 and e["count"] >= 4 and 0 <= e["start"] <= n - 4:
            versions.add(struct.unpack_from("<I", data, e["start"])[0])
    # the blocks, in order, with nothing between them
    pos = 0
    for start, count in ranges:
        if start != pos:
            bad.append(f"a block starts at {start}, expected {pos}")
        pos = start + count
    if pos != n:
        bad.append(f"the blocks end at {pos} of {n} bytes")
    for arr in ("meshes", "images"):
        for k, pp in enumerate(v[arr]):
            r = pp["patchPoint"]["resourceDataRange"]
            if not any(s <= r["start"] and r["start"] + r["count"] <= s + c for s, c in ranges):
                bad.append(f"{arr}[{k}]: {r} is outside every block")
    for arr in ("springs", "models", "decals", "lightFilters", "sounds", "curves", "effectJointCollections"):
        if v[arr]:
            bad.append(f"{arr} is not empty")
    legacy = versions == {1}
    if versions not in ({1}, {2}):
        bad.append(f"unsupported or mixed Kiln particle ABIs: {sorted(versions)}")
    if legacy:
        if v["parameters"] or any(v["defaultParameterData"]["data"]):
            bad.append("ABI 1 must have no play parameters and zero defaults")
        return bad
    # The per-play parameters every Kiln VFX describes (KX_* in native/services/vfx/shaders/kiln_vfx_params.h), and their defaults.
    fnv = lambda s: __import__("functools").reduce(lambda h, b: ((h ^ b) * 0x01000193) & 0xFFFFFFFF, s.encode(), 0x811C9DC5)
    want = [(fnv("color"), 0x53039620, 0, 4), (fnv("size"), 0x90B5085D, 4, 4), (fnv("speed"), 0x90B5085D, 8, 4),
            (fnv("brightness"), 0x90B5085D, 12, 4), (fnv("count"), 0x90B5085D, 16, 4)]
    def _val(x):
        return x["value"] if isinstance(x, dict) else x
    got = [(_val(p["nameHash"]), _val(p["typeId"]), p["dataRange"]["start"], p["dataRange"]["count"]) for p in v["parameters"]]
    if got != want:
        bad.append(f"parameters are {got}, expected Kiln's {want}")
    if list(v["defaultParameterData"]["data"]) != [0xFFFFFFFF, 0x3F800000, 0x3F800000, 0x3F800000, 0x3F800000] + [0] * 10 + [0x4E4C494B]:
        bad.append("defaultParameterData is not Kiln's defaults")
    return bad


def half_to_float(h):
    return struct.unpack("<e", struct.pack("<H", h))[0]


def mesh_positions(blob, vertices):
    return [struct.unpack_from("<3f", blob, i * 28) for i in range(vertices)]


def check_mesh(m, blob):
    """Problems of a decoded VfxMeshResource and its blob."""
    bad = []
    v, i = m["vertexCount"], m["indexCount"]
    ref = m["meshData"]
    if ref["hash"]["size"] != len(blob):
        bad.append(f"the reference says {ref['hash']['size']} bytes, the blob has {len(blob)}")
    if ref["category"]["value"] != MESH_CATEGORY or ref["bundleId"] != 0 or ref["sortKey"] != 0:
        bad.append("meshData reference fields")
    if len(blob) % 28:
        bad.append(f"{len(blob)} bytes is not a multiple of 28: every mesh loaded after it would shift")
    if len(blob) < 28 * v + 2 * i or len(blob) - (28 * v + 2 * i) >= 28:
        bad.append(f"{len(blob)} bytes for {v} vertices and {i} indices")
        return bad
    if v == 0 or i % 3:
        bad.append(f"{v} vertices, {i} indices")
    indices = struct.unpack_from(f"<{i}H", blob, 28 * v)
    if indices and max(indices) >= v:
        bad.append(f"index {max(indices)} of {v} vertices")
    if any(blob[28 * v + 2 * i:]):
        bad.append("the padding is not zero")
    s = m["boundingSphere"]
    for p in mesh_positions(blob, v):
        d = ((p[0] - s["x"]) ** 2 + (p[1] - s["y"]) ** 2 + (p[2] - s["z"]) ** 2) ** 0.5
        if d > s["w"] * 1.01 + 1e-3:
            bad.append(f"a vertex at {p} is outside the bounding sphere {s}")
            break
    return bad


def mip_sizes(width, height, levels, block=16):
    return [((max(1, width >> k) + 3) // 4) * ((max(1, height >> k) + 3) // 4) * block for k in range(levels)]


def expected_ranges(sizes):
    """One range per level with more than 4 KiB left to the end of the blob, each running to the end."""
    total, out, cum = sum(sizes), [], 0
    for s in sizes:
        if total - cum > 4096:
            out.append({"start": cum, "count": total - cum})
        cum += s
    return out


def check_texture(r, blob_size=None):
    """Problems of a decoded RenderTextureResource a VFX names. 2D textures also get their blob size and mip ranges checked."""
    bad = []
    if not r["bindless"]:
        bad.append("not bindless: an image patch point needs a bindless index")
    if r["isTiled"]:
        bad.append("tiled")
    if r["format"] not in BC_BYTES:
        bad.append(f"format {r['format']}")
    if r["content"]["category"]["value"] != TEXTURE_CATEGORY or r["content"]["bundleId"] != 0 or r["content"]["sortKey"] != 0:
        bad.append("content reference fields")
    if blob_size is not None and blob_size != r["content"]["hash"]["size"]:
        bad.append(f"the reference says {r['content']['hash']['size']} bytes, the blob has {blob_size}")
    if r["type"] == "Texture2D" and r["format"] in BC_BYTES:
        sizes = mip_sizes(r["width"], r["height"], r["levelCount"], BC_BYTES[r["format"]])
        if sum(sizes) != r["content"]["hash"]["size"]:
            bad.append(f"{r['width']}x{r['height']} with {r['levelCount']} levels should be {sum(sizes)} bytes")
        elif r["mipChainContentRanges"] != expected_ranges(sizes):
            bad.append(f"mip ranges {r['mipChainContentRanges']}")
    return bad


def check_module(code):
    """Problems of a particle module's SPIR-V: what pipeline creation in the game expects."""
    from .spirv import Module
    bad = []
    m = Module(code)
    if m.version > (1, 5):
        bad.append(f"SPIR-V {m.version}")
    if m.entry_points != [(5, "cs_main")]:
        bad.append(f"entry points {m.entry_points}")
    if m.local_size != (32, 1, 1):
        bad.append(f"LocalSize {m.local_size}")
    if not m.capabilities <= {1, 61, 63, 4466}:
        bad.append(f"capabilities {sorted(m.capabilities)}")
    if not m.has_push_constants():
        bad.append("no push constants")
    return bad
