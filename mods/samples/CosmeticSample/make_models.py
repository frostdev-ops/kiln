#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes the sample's models with no game data and no tools but Python: a hood and a cape, each a plain mesh with no skin, around the origin. `attach` in the block puts the origin on a joint.

    python3 mods/samples/CosmeticSample/make_models.py

The axes are the character's: y up, the wearer faces +z, their left is +x. Both have a morph target named Female, a slimmer fit, and one material.
"""
import base64
import json
import math
import struct
from pathlib import Path

HERE = Path(__file__).resolve().parent


def write_gltf(path, name, verts, tris, female, material, color):
    """One primitive, one morph target, data in a data URI: a file a person can read."""
    normals = [(0.0, 0.0, 0.0) for _ in verts]
    for a, b, c in tris:
        ux, uy, uz = (verts[b][i] - verts[a][i] for i in range(3))
        vx, vy, vz = (verts[c][i] - verts[a][i] for i in range(3))
        n = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx)
        for i in (a, b, c):
            normals[i] = tuple(normals[i][k] + n[k] for k in range(3))
    normals = [tuple(x / (math.sqrt(sum(c * c for c in n)) or 1.0) for x in n) for n in normals]
    blob = bytearray()
    views, accessors = [], []

    def add(data, typ, comp, count, target=None, bounds=False, rows=None):
        while len(blob) % 4:
            blob.append(0)
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), **({"target": target} if target else {})})
        blob.extend(data)
        a = {"bufferView": len(views) - 1, "componentType": comp, "count": count, "type": typ}
        if bounds:
            a["min"] = [min(r[i] for r in rows) for i in range(3)]
            a["max"] = [max(r[i] for r in rows) for i in range(3)]
        accessors.append(a)
        return len(accessors) - 1

    pos = add(b"".join(struct.pack("<3f", *v) for v in verts), "VEC3", 5126, len(verts), 34962, True, verts)
    nrm = add(b"".join(struct.pack("<3f", *n) for n in normals), "VEC3", 5126, len(verts), 34962)
    uv = add(b"".join(struct.pack("<2f", 0.5 + v[0], 0.5 - v[1]) for v in verts), "VEC2", 5126, len(verts), 34962)
    deltas = [(f[0] - v[0], f[1] - v[1], f[2] - v[2]) for v, f in zip(verts, female)]
    target = add(b"".join(struct.pack("<3f", *d) for d in deltas), "VEC3", 5126, len(verts), 34962, True, deltas)
    idx = add(b"".join(struct.pack("<3H", *t) for t in tris), "SCALAR", 5123, 3 * len(tris), 34963)
    doc = {
        "asset": {"version": "2.0", "generator": "CosmeticSample make_models.py"},
        "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"name": name, "mesh": 0}],
        "meshes": [{"name": name, "extras": {"targetNames": ["Female"]},
                    "primitives": [{"attributes": {"POSITION": pos, "NORMAL": nrm, "TEXCOORD_0": uv}, "indices": idx, "material": 0, "targets": [{"POSITION": target}]}]}],
        "materials": [{"name": material, "doubleSided": True, "pbrMetallicRoughness": {"baseColorFactor": [*color, 1.0], "metallicFactor": 0.0, "roughnessFactor": 0.85}}],
        "bufferViews": views, "accessors": accessors,
        "buffers": [{"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()}],
    }
    path.write_text(json.dumps(doc))
    print(f"{path.relative_to(HERE)}: {len(verts)} vertices, {len(tris)} triangles")


def grid(columns, rows, at):
    """A sheet of vertices from at(u, v), u and v in 0..1, wound so that its normal faces the side from which u runs right and v down."""
    verts = [at(c / columns, r / rows) for r in range(rows + 1) for c in range(columns + 1)]
    w = columns + 1
    tris = []
    for r in range(rows):
        for c in range(columns):
            a, b, d, e = r * w + c, r * w + c + 1, (r + 1) * w + c, (r + 1) * w + c + 1
            tris += [(a, d, b), (b, d, e)]
    return verts, tris


def hood(slim):
    """Over the head and down to the neck: a dome from the crown, open at the front. Around the head joint, which sits low in the skull."""
    rx, rz, ry = (0.128, 0.142, 0.150) if not slim else (0.118, 0.132, 0.145)

    def at(u, v):
        # u walks from the left cheek around the back to the right cheek, v from the crown to the shoulders. 0 degrees is the face, and the gap
        around = math.radians(40 + 280 * u)
        down = math.radians(4 + 112 * v)
        s = math.sin(down)
        flare = 1 + 0.35 * max(0.0, v - 0.6)
        return (rx * flare * s * math.sin(around), 0.085 + ry * math.cos(down), rz * flare * s * math.cos(around) - 0.01)

    return grid(28, 14, at)


def cape(slim):
    """Off the shoulders, down the back to the knees. Around the chest joint, whose height is the shoulder blades'."""
    half = 0.25 if not slim else 0.22

    def at(u, v):
        x = half * (2 * u - 1) * (1 + 0.45 * v)
        y = 0.12 - 0.95 * v
        z = -0.17 - 0.12 * v - 0.02 * math.cos(9 * (u - 0.5)) * v
        return (x, y, z)

    return grid(12, 18, at)


def main():
    models = HERE / "models"
    models.mkdir(exist_ok=True)
    for name, shape, material, color in (("frost-hood", hood, "frost_cloth", (0.28, 0.45, 0.70)), ("wander-cape", cape, "wander_wool", (0.42, 0.18, 0.12))):
        verts, tris = shape(False)
        female, _ = shape(True)
        write_gltf(models / f"{name}.gltf", name, verts, tris, female, material, color)


if __name__ == "__main__":
    main()
