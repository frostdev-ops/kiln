# SPDX-License-Identifier: MIT
"""Writes models/lantern.glb: a box 0.4 m wide, 0.8 m tall, 0.3 m deep standing on its origin, one material. No outside data; run it to make the file again."""
import json
import struct
from pathlib import Path


def box(w, h, d):
    x, z = w / 2, d / 2
    faces = [((0, 0, 1), [(-x, 0, z), (x, 0, z), (x, h, z), (-x, h, z)]), ((0, 0, -1), [(x, 0, -z), (-x, 0, -z), (-x, h, -z), (x, h, -z)]),
             ((1, 0, 0), [(x, 0, z), (x, 0, -z), (x, h, -z), (x, h, z)]), ((-1, 0, 0), [(-x, 0, -z), (-x, 0, z), (-x, h, z), (-x, h, -z)]),
             ((0, 1, 0), [(-x, h, z), (x, h, z), (x, h, -z), (-x, h, -z)]), ((0, -1, 0), [(-x, 0, -z), (x, 0, -z), (x, 0, z), (-x, 0, z)])]
    pos, nor, uv, idx = [], [], [], []
    for n, quad in faces:
        b = len(pos)
        pos += quad
        nor += [n] * 4
        uv += [(0, 1), (1, 1), (1, 0), (0, 0)]
        idx += [b, b + 1, b + 2, b, b + 2, b + 3]
    return pos, nor, uv, idx


def glb(path, w, h, d):
    pos, nor, uv, idx = box(w, h, d)
    blobs = [b"".join(struct.pack("<3f", *p) for p in pos), b"".join(struct.pack("<3f", *n) for n in nor), b"".join(struct.pack("<2f", *t) for t in uv),
             b"".join(struct.pack("<H", i) for i in idx)]
    views, off = [], 0
    for b in blobs:
        views.append({"buffer": 0, "byteOffset": off, "byteLength": len(b)})
        off += (len(b) + 3) & ~3
    data = b"".join(b + b"\0" * (((len(b) + 3) & ~3) - len(b)) for b in blobs)
    doc = {
        "asset": {"version": "2.0", "generator": "blocks-more-test make_models.py"}, "scene": 0, "scenes": [{"nodes": [0]}],
        "nodes": [{"name": "lantern", "mesh": 0}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1, "TEXCOORD_0": 2}, "indices": 3, "material": 0}]}],
        "materials": [{"name": "lantern_glass", "pbrMetallicRoughness": {"baseColorFactor": [1.0, 0.8, 0.4, 1.0], "metallicFactor": 0.0, "roughnessFactor": 0.6}}],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": len(pos), "type": "VEC3", "min": [-w / 2, 0, -d / 2], "max": [w / 2, h, d / 2]},
            {"bufferView": 1, "componentType": 5126, "count": len(nor), "type": "VEC3"},
            {"bufferView": 2, "componentType": 5126, "count": len(uv), "type": "VEC2"},
            {"bufferView": 3, "componentType": 5123, "count": len(idx), "type": "SCALAR"}],
        "bufferViews": views, "buffers": [{"byteLength": len(data)}],
    }
    js = json.dumps(doc, separators=(",", ":")).encode()
    js += b" " * ((4 - len(js) % 4) % 4)
    out = b"glTF" + struct.pack("<II", 2, 12 + 8 + len(js) + 8 + len(data)) + struct.pack("<I", len(js)) + b"JSON" + js + struct.pack("<I", len(data)) + b"BIN\0" + data
    Path(path).write_bytes(out)


if __name__ == "__main__":
    here = Path(__file__).parent
    glb(here / "models" / "lantern.glb", 0.4, 0.8, 0.3)
