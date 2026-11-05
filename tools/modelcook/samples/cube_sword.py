#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes a procedural test sword as glTF 2.0 (no game data): a blade of five stacked cubes, a box crossguard, a box
grip, a cube pommel, and a right-handed spiral of small cubes around the blade (a chirality marker: on screen the
strands nearest to the camera rise to the right, "/", whenever the sword is shown unmirrored, from any side).

Conventions of Kiln weapon models (docs/kiln/modding/models.md): +Y up the blade, origin = the hand grip, metres;
the vanilla one-handed sword spans y -0.17 .. 0.94, crossguard along z.
UVs point into the regions of the vanilla sword texture (blade: plain steel, grip: leather wrap), so the sword can
keep the vanilla material.

    python3 tools/modelcook/samples/cube_sword.py OUT.gltf [cubes|tower]
"""
import base64
import json
import math
import struct
import sys

STEEL = (0.10, 0.40, 0.50, 0.85)    # u0, v0, u1, v1
DARK = (0.06, 0.03, 0.44, 0.14)
LEATHER = (0.72, 0.26, 0.94, 0.50)

FACES = [  # normal, then the four corners as (sx, sy, sz) signs, counter-clockwise seen from outside
    ((1, 0, 0), [(1, -1, 1), (1, -1, -1), (1, 1, -1), (1, 1, 1)]),
    ((-1, 0, 0), [(-1, -1, -1), (-1, -1, 1), (-1, 1, 1), (-1, 1, -1)]),
    ((0, 1, 0), [(-1, 1, 1), (1, 1, 1), (1, 1, -1), (-1, 1, -1)]),
    ((0, -1, 0), [(-1, -1, -1), (1, -1, -1), (1, -1, 1), (-1, -1, 1)]),
    ((0, 0, 1), [(-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]),
    ((0, 0, -1), [(1, -1, -1), (-1, -1, -1), (-1, 1, -1), (1, 1, -1)]),
]


def rot_y(v, a):
    c, s = math.cos(a), math.sin(a)
    return (c * v[0] + s * v[2], v[1], -s * v[0] + c * v[2])


def box(P, N, UV, I, center, half, uvr, yaw=0.0):
    for n, corners in FACES:
        base = len(P)
        for k, (sx, sy, sz) in enumerate(corners):
            local = (sx * half[0], sy * half[1], sz * half[2])
            p = rot_y(local, yaw)
            P.append((center[0] + p[0], center[1] + p[1], center[2] + p[2]))
            N.append(rot_y(n, yaw))
            u = uvr[0] if k in (0, 3) else uvr[2]
            v = uvr[3] if k in (0, 1) else uvr[1]
            UV.append((u, v))
        I += [base, base + 1, base + 2, base, base + 2, base + 3]


def build(variant="cubes"):
    P, N, UV, I = [], [], [], []
    if variant == "cubes":  # blade: five cubes, every second one turned by 45 degrees
        for i in range(5):
            y = 0.17 + i * 0.165
            box(P, N, UV, I, (0, y, 0), (0.035, 0.07, 0.035), STEEL, yaw=math.radians(45 if i % 2 else 0))
    else:  # "tower": three big turned cubes and a flat slab at the tip (tells a new model from the override)
        for i in range(3):
            y = 0.22 + i * 0.25
            box(P, N, UV, I, (0, y, 0), (0.06, 0.11, 0.06), STEEL, yaw=math.radians(45))
        box(P, N, UV, I, (0, 0.98, 0), (0.02, 0.04, 0.12), STEEL)
    box(P, N, UV, I, (0, 0.10, 0), (0.025, 0.02, 0.16), DARK)        # crossguard (along z, like vanilla)
    box(P, N, UV, I, (0, -0.03, 0), (0.017, 0.11, 0.017), LEATHER)    # grip, origin inside it
    box(P, N, UV, I, (0, -0.17, 0), (0.03, 0.03, 0.03), DARK)        # pommel
    # right-handed spiral: p(t) = (r cos t, y0 + k t, -r sin t), rising counter-clockwise seen from the tip
    r, y0, k = 0.075, 0.12, 0.85 / (4 * math.pi)
    for j in range(32):
        t = j * (4 * math.pi) / 31
        box(P, N, UV, I, (r * math.cos(t), y0 + k * t, -r * math.sin(t)), (0.009, 0.009, 0.009), DARK)
    return P, N, UV, I


def write(path, variant="cubes"):
    P, N, UV, I = build(variant)
    blob = bytearray()
    views, acc = [], []

    def add(data, target, comp, typ, count, mn=None, mx=None):
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), "target": target})
        blob.extend(data)
        a = {"bufferView": len(views) - 1, "componentType": comp, "count": count, "type": typ}
        if mn is not None:
            a["min"], a["max"] = mn, mx
        acc.append(a)
        return len(acc) - 1

    pa = add(b"".join(struct.pack("<3f", *p) for p in P), 34962, 5126, "VEC3", len(P),
             [min(p[i] for p in P) for i in range(3)], [max(p[i] for p in P) for i in range(3)])
    na = add(b"".join(struct.pack("<3f", *n) for n in N), 34962, 5126, "VEC3", len(N))
    ua = add(b"".join(struct.pack("<2f", *uv) for uv in UV), 34962, 5126, "VEC2", len(UV))
    ia = add(struct.pack(f"<{len(I)}H", *I), 34963, 5123, "SCALAR", len(I))
    doc = {
        "asset": {"version": "2.0", "generator": "kiln modelcook samples/cube_sword.py"},
        "scene": 0, "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0, "name": "kiln_cube_sword"}],
        "materials": [{"name": "sword", "pbrMetallicRoughness": {"baseColorFactor": [0.8, 0.8, 0.8, 1]}}],
        "meshes": [{"name": "kiln_cube_sword", "primitives": [
            {"attributes": {"POSITION": pa, "NORMAL": na, "TEXCOORD_0": ua}, "indices": ia, "material": 0}]}],
        "buffers": [{"byteLength": len(blob),
                     "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()}],
        "bufferViews": views, "accessors": acc,
    }
    with open(path, "w") as f:
        json.dump(doc, f)
    return len(P), len(I) // 3


if __name__ == "__main__":
    if len(sys.argv) not in (2, 3):
        print(__doc__)
        sys.exit(2)
    v, t = write(sys.argv[1], sys.argv[2] if len(sys.argv) == 3 else "cubes")
    print(f"{sys.argv[1]}: {v} vertices, {t} triangles")
