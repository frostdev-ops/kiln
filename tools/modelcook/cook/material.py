# SPDX-License-Identifier: MIT
"""Materials of a model step. A model draws with a game material by GUID, with a MaterialBlock of the mod by name (the "materials" section of
blocks/*.json, docs/kiln/modding/materials.md), or with blocks made here from the glTF's own materials (`"materials": "gltf"`).

kiln.vfx builds the blocks at boot from the same JSON. A model only needs a block's GUID and the flags that pick its pipeline, so they are worked out
here; tools/modelcook/tests holds this table to the builder's.
"""
import json
import re
import struct
import uuid
from pathlib import Path

from . import gltf

NAMESPACE = uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77")  # Kiln.Blocks' BlockIds
GUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"
WRAP = 10497
# RenderMaterialFlag names by what a kind turns on
KINDS = {
    "opaque": {},
    "metal": {},
    "cutout": {"alphaTest": True},
    "foliage": {"alphaTest": True, "doubleSided": True, "translucent": 1},
    "cloth": {"doubleSided": True},
    "emissive": {},
    "dithered": {"hashed": True},
}


class MaterialError(Exception):
    pass


def block_guid(mod, name):
    return str(uuid.uuid5(NAMESPACE, f"{mod}:material:{name}"))


def flags_of(block):
    """keen::RenderMaterialFlag names, as the builder sets them. Those that reach a model's pipelineBits decide what it draws with."""
    kind = KINDS.get(block.get("kind", "opaque"), {})
    alpha = block.get("alphaTest", kind.get("alphaTest", False))
    hashed = kind.get("hashed", False)
    out = []
    if alpha is not False or hashed:
        out.append("AlphaTested")
    if block.get("doubleSided", kind.get("doubleSided", False)):
        out.append("DoubleSided")
    if block.get("shadows", True) is False:
        out.append("DisableShadows")
    if block.get("dissolve", False) is not False:
        out.append("CanDissolve")
    if block.get("translucent", kind.get("translucent", 0)) > 0:
        out.append("Translucent")
    out.append("Foliage")
    if hashed:
        out.append("HashedAlpha")
    return out


def _relaxed(text):
    """blocks/*.json may hold // and /* */ comments and a comma before } or ], as kiln.vfx's parser takes them."""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            out.append(text[i:j + 1])
            i = j + 1
        elif text.startswith("//", i):
            while i < n and text[i] != "\n":
                i += 1
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            i = n if end < 0 else end + 2
        else:
            out.append(c)
            i += 1
    return re.sub(r",(\s*[}\]])", r"\1", "".join(out))


def mod_id(doc, mod_dir):
    """What a block's id follows from: the recipe's "mod", or the mod's manifest."""
    if doc.get("mod"):
        return doc["mod"]
    manifest = Path(mod_dir) / "kiln.mod.json"
    if not manifest.exists():
        raise MaterialError(f"no \"mod\" in the recipe and no {manifest}: a block's id needs the mod's id")
    return json.loads(_relaxed(manifest.read_text()))["id"]


def read_blocks(mod_dir):
    """Every material block of a mod's blocks/*.json: {name: (block, file)}."""
    blocks = {}
    for f in sorted((Path(mod_dir) / "blocks").glob("*.json")):
        try:
            doc = json.loads(_relaxed(f.read_text()))
        except ValueError as e:
            raise MaterialError(f"{f}: {e}") from e
        for name, block in (doc.get("materials") or {}).items():
            blocks.setdefault(name, (block, f))
    return blocks


def entry(name, blocks, mod):
    """The RenderModelMaterial of a block by name, or None when there's no such block."""
    if name not in blocks:
        return None
    block = blocks[name][0]
    return {"material": block_guid(mod, block.get("was") or name), "renderEffectId": "Pbr", "materialFlags": flags_of(block)}


# ---- a glTF material -> a block ---------------------------------------------------------------------------------------------------------------
def _hex(rgb):
    """Linear glTF factors -> "#rrggbb" in sRGB."""
    def channel(v):
        v = max(0.0, min(1.0, float(v)))
        s = v * 12.92 if v <= 0.0031308 else 1.055 * v ** (1 / 2.4) - 0.055
        return int(round(s * 255))
    return "#" + "".join(f"{channel(v):02x}" for v in rgb[:3])


def _png_size(data):
    return struct.unpack(">II", data[16:24])


def _sane(name):
    return re.sub(r"[^A-Za-z0-9_.-]", "_", name)


class _Images:
    """A glTF's images as files in the mod: a PNG beside the glTF stays where it is, an embedded one is written out once."""

    def __init__(self, doc, buffers, gltf_path, mod_dir, out_rel, stem):
        self.doc, self.buffers, self.gltf_path, self.mod_dir, self.out_rel, self.stem = doc, buffers, Path(gltf_path), Path(mod_dir), out_rel, stem
        self.files = {}  # relative to the mod -> bytes to write
        self.warnings = []

    def _bytes(self, image):
        if "bufferView" in image:
            view = self.doc["bufferViews"][image["bufferView"]]
            at = view.get("byteOffset", 0)
            return bytes(self.buffers[view.get("buffer", 0)][at:at + view["byteLength"]])
        uri = image.get("uri", "")
        if uri.startswith("data:"):
            import base64
            return base64.b64decode(uri.split(",", 1)[1])
        return (self.gltf_path.parent / uri).read_bytes()

    def path(self, texture_info, where):
        """A textureInfo -> a PNG's path relative to the mod."""
        tex = self.doc["textures"][texture_info["index"]]
        if texture_info.get("texCoord", 0) != 0:
            raise MaterialError(f"{where}: uses UV set {texture_info['texCoord']}: the game's models have one")
        sampler = self.doc.get("samplers", [{}])[tex["sampler"]] if "sampler" in tex else {}
        if sampler.get("wrapS", WRAP) != WRAP or sampler.get("wrapT", WRAP) != WRAP:
            self.warnings.append(f"{where}: its sampler doesn't repeat, and a game material's always does")
        if "source" not in tex:
            raise MaterialError(f"{where}: its texture names no image")
        index = tex["source"]
        image = self.doc["images"][index]
        data = self._bytes(image)
        name = image.get("name") or Path(image.get("uri", "")).stem or f"image{index}"
        if data[:8] != PNG_MAGIC:
            raise MaterialError(f"{where}: image {name!r} is not a PNG: export PNG textures")
        w, h = _png_size(data)
        if w & (w - 1) or h & (h - 1) or min(w, h) < 4 or max(w, h) > 4096:
            raise MaterialError(f"{where}: image {name!r} is {w}x{h}: a material's textures are a power of two on both sides, 4 to 4096")
        uri = image.get("uri", "")
        if uri and not uri.startswith("data:"):
            full = (self.gltf_path.parent / uri).resolve()
            try:
                return full.relative_to(self.mod_dir.resolve()).as_posix()
            except ValueError:
                raise MaterialError(f"{where}: image {uri!r} is outside the mod's folder") from None
        rel = f"{self.out_rel}/{self.stem}/{_sane(name)}.png"
        self.files[rel] = data
        return rel


def _transform_scale(info, into, where, warnings):
    t = (info.get("extensions") or {}).get("KHR_texture_transform")
    if not t:
        return
    if t.get("offset", [0, 0]) != [0, 0] or t.get("rotation", 0):
        warnings.append(f"{where}: a texture offset or rotation has no place in a game material: only its scale is kept")
    if t.get("scale"):
        into.setdefault("uvScale", list(t["scale"]))


def block_from_gltf(doc, index, images, has_vertex_colors, where):
    """One entry of a glTF's "materials" as a MaterialBlock. What the game can't draw warns by name."""
    m = doc["materials"][index]
    warnings = images.warnings
    pbr = m.get("pbrMetallicRoughness") or {}
    block = {}
    factor = pbr.get("baseColorFactor", [1, 1, 1, 1])
    if pbr.get("baseColorTexture"):
        block["albedo"] = images.path(pbr["baseColorTexture"], f"{where}.baseColorTexture")
        _transform_scale(pbr["baseColorTexture"], block, f"{where}.baseColorTexture", warnings)
    if list(factor[:3]) != [1, 1, 1] or "albedo" not in block:
        block["tint"] = _hex(factor)
    if pbr.get("metallicRoughnessTexture"):
        block["metallicRoughness"] = images.path(pbr["metallicRoughnessTexture"], f"{where}.metallicRoughnessTexture")
        if pbr.get("roughnessFactor", 1) != 1:
            block["roughness"] = pbr["roughnessFactor"]
        if pbr.get("metallicFactor", 1) != 1:
            block["metallic"] = pbr["metallicFactor"]
    else:
        block["roughness"] = pbr.get("roughnessFactor", 1)
        block["metallic"] = pbr.get("metallicFactor", 1)
    if m.get("occlusionTexture"):
        block["occlusion"] = images.path(m["occlusionTexture"], f"{where}.occlusionTexture")
        if m["occlusionTexture"].get("strength", 1) != 1:
            warnings.append(f"{where}.occlusionTexture: its strength is not kept: bake it into the image")
    if m.get("normalTexture"):
        path = images.path(m["normalTexture"], f"{where}.normalTexture")
        scale = m["normalTexture"].get("scale", 1)
        block["normal"] = path if scale == 1 else {"map": path, "strength": scale}
    emissive = m.get("emissiveFactor", [0, 0, 0])
    strength = ((m.get("extensions") or {}).get("KHR_materials_emissive_strength") or {}).get("emissiveStrength", 1)
    if m.get("emissiveTexture") or any(v > 0 for v in emissive):
        e = {"color": _hex(emissive), "strength": strength}
        if m.get("emissiveTexture"):
            e["map"] = images.path(m["emissiveTexture"], f"{where}.emissiveTexture")
        block["emissive"] = e
    kind = "opaque"
    mode = m.get("alphaMode", "OPAQUE")
    if mode == "MASK":
        kind = "cutout"
        block["alphaTest"] = m.get("alphaCutoff", 0.5)
    elif mode == "BLEND":
        kind = "dithered"
        warnings.append(f"{where}: alphaMode BLEND: the game draws no transparent surface from a material's data, so it is dithered until surface shaders exist")
    elif "emissive" in block:
        kind = "emissive"
    elif block.get("metallic", 0) >= 0.5 and "metallicRoughness" not in block:
        kind = "metal"
    if kind != "opaque":
        block["kind"] = kind
    if m.get("doubleSided"):
        block["doubleSided"] = True
    if has_vertex_colors:
        block["vertexColor"] = True
    if (m.get("extensions") or {}).get("KHR_materials_unlit") is not None:
        warnings.append(f"{where}: KHR_materials_unlit has no game shader: it draws lit")
    return block


def _vertex_colored(doc, buffers):
    """Material indices whose primitives carry a COLOR_0 that isn't all white."""
    out = set()
    for mesh in doc.get("meshes", []):
        for p in mesh.get("primitives", []):
            if "COLOR_0" in p.get("attributes", {}) and p.get("material") is not None:
                if any(any(c < 0.999 for c in v[:3]) for v in gltf._accessor(doc, buffers, p["attributes"]["COLOR_0"])):
                    out.add(p["material"])
    return out


def from_gltf(gltf_path, mod_dir, stem, out_rel):
    """Blocks for every material of a glTF.
    -> ({glTF material name: block name}, {block name: block}, {path relative to the mod: PNG bytes}, warnings). Names are <stem>.<material>."""
    doc, buffers = gltf._load(gltf_path)
    images = _Images(doc, buffers, gltf_path, mod_dir, out_rel, stem)
    colored = _vertex_colored(doc, buffers)
    names, blocks = {}, {}
    for i, m in enumerate(doc.get("materials", [])):
        key = m.get("name", f"material{i}")
        name = f"{_sane(stem)}.{_sane(key)}"
        blocks[name] = block_from_gltf(doc, i, images, i in colored, f"glTF material {key!r}")
        names[key] = name
    return names, blocks, images.files, images.warnings


def blocks_file(blocks, source):
    doc = {"format": 1, "comment": f"Made by tools/modelcook from {source}. Change the glTF and cook again, or copy a block to a file of your own and name it in the recipe.",
           "materials": blocks}
    return json.dumps(doc, indent=2) + "\n"
