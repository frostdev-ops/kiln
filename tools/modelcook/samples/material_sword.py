#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes MaterialSample's art, all of it made here (no game data, stdlib only).

    python3 tools/modelcook/samples/material_sword.py MOD_FOLDER

    MOD_FOLDER/textures/steel.png         albedo: brushed blue steel
    MOD_FOLDER/textures/steel_n.png       normal: scratches along the blade, green up
    MOD_FOLDER/textures/steel_mr.png      metallicRoughness, glTF's channels: G roughness, B metallic
    MOD_FOLDER/textures/runes.png         emissive: a line of angular runes
    MOD_FOLDER/assets/src/frost_sword.gltf  materials "blade" and "grip": the recipe maps them to blocks by name
    MOD_FOLDER/assets/src/ember_sword.gltf  full glTF PBR materials with embedded PNGs: the recipe makes blocks of them

A blade along +Y with its grip at the origin, as docs/kiln/modding/models.md says, with UVs running the length of the blade.
"""
import base64
import json
import math
import struct
import sys
import zlib
from pathlib import Path

SIZE = 256


def png(fn, size=SIZE):
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
    rows = bytearray()
    for y in range(size):
        rows.append(0)
        for x in range(size):
            rows += bytes(max(0, min(255, int(round(c)))) for c in fn(x / size, y / size))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b"")


def noise(v):
    return math.sin(v * 127.1) * 43758.5453 % 1.0


def scratch(u, v):
    """Fine lines along the blade (v), slightly different per column."""
    n = noise(math.floor(u * 64))
    return n * math.sin(v * 12 + n * 40) * 0.5 + 0.5


def rune(u, v):
    """1 inside a line of angular glyphs down the blade's middle, else 0."""
    if abs(u - 0.5) > 0.12:
        return 0.0
    cell = math.floor(v * 8)
    fv = v * 8 - cell
    fu = (u - 0.38) / 0.24
    s = noise(cell + 3)
    strokes = (abs(fu - fv) < 0.09, abs(fu - (1 - fv)) < 0.09 and s > 0.4, abs(fu - 0.5) < 0.07 and s > 0.25, abs(fv - 0.5) < 0.07 and s > 0.7)
    return 1.0 if any(strokes) and 0.08 < fv < 0.92 else 0.0


def steel(u, v):
    s = scratch(u, v)
    return (70 + 40 * s, 110 + 50 * s, 170 + 60 * s, 255)


def steel_normal(u, v):
    d = (scratch(u + 1 / 64, v) - scratch(u, v)) * 40
    return (128 + d, 128, 255, 255)


def steel_mr(u, v):
    return (0, 60 + 70 * scratch(u, v), 255, 255)


def runes(u, v):
    g = rune(u, v)
    return (255 * g, 255 * g, 255 * g, 255)


def ember(u, v):
    s = scratch(u, v)
    return (60 + 30 * s, 40 + 15 * s, 35 + 10 * s, 255)


def ember_glow(u, v):
    c = abs(math.sin(v * 22 + noise(math.floor(u * 8)) * 6))
    g = max(0.0, (c - 0.82) / 0.18) * (1.0 if 0.2 < u < 0.8 else 0.0)
    return (255 * g, 120 * g, 30 * g, 255)


def box(pos, nrm, uv, idx, center, half, uv_box):
    """A box with UVs running along its length: u across, v along y."""
    faces = [((1, 0, 0), (0, 0, -1), (0, 1, 0)), ((-1, 0, 0), (0, 0, 1), (0, 1, 0)), ((0, 0, 1), (1, 0, 0), (0, 1, 0)), ((0, 0, -1), (-1, 0, 0), (0, 1, 0)),
             ((0, 1, 0), (1, 0, 0), (0, 0, -1)), ((0, -1, 0), (1, 0, 0), (0, 0, 1))]
    for n, t, b in faces:
        base = len(pos)
        for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            pos.append(tuple(center[i] + half[i] * (n[i] + su * t[i] + sv * b[i]) for i in range(3)))
            nrm.append(n)
            uv.append((uv_box[0] + (su + 1) / 2 * (uv_box[2] - uv_box[0]), uv_box[3] - (sv + 1) / 2 * (uv_box[3] - uv_box[1])))
        idx += [base, base + 1, base + 2, base, base + 2, base + 3]


def mesh_of(boxes):
    pos, nrm, uv, idx = [], [], [], []
    for center, half, uv_box in boxes:
        box(pos, nrm, uv, idx, center, half, uv_box)
    return pos, nrm, uv, idx


BLADE = [((0, 0.5, 0), (0.012, 0.45, 0.055), (0, 0, 1, 1)), ((0, 1.0, 0), (0.008, 0.05, 0.03), (0, 0, 1, 0.1))]
GRIP = [((0, 0.03, 0), (0.02, 0.03, 0.13), (0, 0, 1, 1)), ((0, -0.09, 0), (0.018, 0.09, 0.018), (0, 0, 1, 1)), ((0, -0.19, 0), (0.024, 0.02, 0.024), (0, 0, 1, 1))]


def gltf(materials, images=None, textures=None):
    """A glTF with two meshes, the blade and the grip, each its own material."""
    buf = bytearray()
    views, accessors, primitives = [], [], []

    def add(data, target=None):
        while len(buf) % 4:
            buf.append(0)
        v = {"buffer": 0, "byteOffset": len(buf), "byteLength": len(data)}
        if target:
            v["target"] = target
        buf.extend(data)
        views.append(v)
        return len(views) - 1

    for i, boxes in enumerate((BLADE, GRIP)):
        pos, nrm, uv, idx = mesh_of(boxes)
        a0 = len(accessors)
        accessors.append({"bufferView": add(b"".join(struct.pack("<3f", *p) for p in pos), 34962), "componentType": 5126, "count": len(pos), "type": "VEC3",
                          "min": [min(p[k] for p in pos) for k in range(3)], "max": [max(p[k] for p in pos) for k in range(3)]})
        accessors.append({"bufferView": add(b"".join(struct.pack("<3f", *n) for n in nrm), 34962), "componentType": 5126, "count": len(nrm), "type": "VEC3"})
        accessors.append({"bufferView": add(b"".join(struct.pack("<2f", *t) for t in uv), 34962), "componentType": 5126, "count": len(uv), "type": "VEC2"})
        accessors.append({"bufferView": add(struct.pack(f"<{len(idx)}H", *idx), 34963), "componentType": 5123, "count": len(idx), "type": "SCALAR"})
        primitives.append({"attributes": {"POSITION": a0, "NORMAL": a0 + 1, "TEXCOORD_0": a0 + 2}, "indices": a0 + 3, "material": i})
    doc = {"asset": {"version": "2.0", "generator": "tools/modelcook/samples/material_sword.py"}, "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0, "name": "sword"}],
           "meshes": [{"primitives": primitives}], "materials": materials, "accessors": accessors, "bufferViews": views}
    if images:
        doc["images"] = []
        for name, data in images:
            doc["images"].append({"name": name, "mimeType": "image/png", "bufferView": add(data)})
        doc["textures"] = [{"source": i} for i in range(len(images))]
    doc["buffers"] = [{"byteLength": len(buf), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(buf)).decode()}]
    return json.dumps(doc)


def write(mod):
    mod = Path(mod)
    (mod / "textures").mkdir(parents=True, exist_ok=True)
    (mod / "assets" / "src").mkdir(parents=True, exist_ok=True)
    (mod / "textures" / "steel.png").write_bytes(png(steel))
    (mod / "textures" / "steel_n.png").write_bytes(png(steel_normal))
    (mod / "textures" / "steel_mr.png").write_bytes(png(steel_mr))
    (mod / "textures" / "runes.png").write_bytes(png(runes))
    (mod / "assets" / "src" / "frost_sword.gltf").write_text(gltf([{"name": "blade"}, {"name": "grip"}]))
    images = [("ember_albedo", png(ember, 64)), ("ember_glow", png(ember_glow, 64))]
    (mod / "assets" / "src" / "ember_sword.gltf").write_text(gltf([
        {"name": "blade", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}, "metallicFactor": 0.9, "roughnessFactor": 0.55},
         "emissiveTexture": {"index": 1}, "emissiveFactor": [1, 1, 1], "extensions": {"KHR_materials_emissive_strength": {"emissiveStrength": 6}}},
        {"name": "grip", "pbrMetallicRoughness": {"baseColorFactor": [0.12, 0.06, 0.04, 1], "metallicFactor": 0, "roughnessFactor": 0.9}},
    ], images=images))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    write(sys.argv[1])
