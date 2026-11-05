#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes a small test creature as glTF 2.0, no game data: a skinned box figure of 9 joints with animations in all of glTF's sampler kinds.

    python3 tools/animcook/samples/make_golem.py OUT.glb [--gltf]          (--gltf: .gltf with an embedded buffer, node matrices)

Used by tools/animcook/tests and mods/tests/animcook-test. Facing +Z, y up, metres. What each animation does is computable by hand:
the tests compare the cooked result with these formulas.
"""
import base64
import json
import math
import struct
import sys

# name, parent, rest translation, rest rotation (degrees about x, y, z)
JOINTS = [("root", None, (0, 0, 0), (0, 0, 0)),
          ("pelvis", "root", (0, 0.9, 0), (0, 0, 0)),
          ("spine", "pelvis", (0, 0.4, 0), (10, 0, 0)),
          ("head", "spine", (0, 0.5, 0), (-10, 0, 0)),
          ("arm_r", "spine", (0.25, 0.35, 0), (0, 0, 20)),
          ("hand_r", "arm_r", (0.6, 0, 0), (0, 0, 0)),
          ("arm_l", "spine", (-0.25, 0.35, 0), (0, 0, -20)),
          ("hand_l", "arm_l", (-0.6, 0, 0), (0, 0, 0)),
          ("leg_r", "pelvis", (0.15, -0.1, 0), (0, 0, 0))]
ARM_SWING_DEG = 30.0
FPS_KEYS = 30


def euler(x, y, z):
    def q(ax, deg):
        h = math.radians(deg) / 2
        v = [0.0, 0.0, 0.0, math.cos(h)]
        v[ax] = math.sin(h)
        return v
    def mul(a, b):
        return [a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1], a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
                a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3], a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]]
    return mul(mul(q(0, x), q(1, y)), q(2, z))


def rest_of(name):
    return next(j for j in JOINTS if j[0] == name)


def arm_rot(name, extra_deg):
    """Rest rotation of an arm, then extra_deg about its local z."""
    r = rest_of(name)[3]
    return euler(r[0], r[1], r[2] + extra_deg)


class Writer:
    def __init__(self):
        self.blob = bytearray()
        self.views, self.accessors = [], []

    def add(self, data, comp, typ, count, target=None, mn=None, mx=None):
        while len(self.blob) % 4:
            self.blob.append(0)
        v = {"buffer": 0, "byteOffset": len(self.blob), "byteLength": len(data)}
        if target:
            v["target"] = target
        self.views.append(v)
        self.blob.extend(data)
        a = {"bufferView": len(self.views) - 1, "componentType": comp, "count": count, "type": typ}
        if mn is not None:
            a["min"], a["max"] = mn, mx
        self.accessors.append(a)
        return len(self.accessors) - 1

    def floats(self, vals):
        return struct.pack(f"<{len(vals)}f", *vals)


def build(as_matrices=False):
    w = Writer()
    names = [j[0] for j in JOINTS]
    node_of = {n: i + 2 for i, n in enumerate(names)}  # node 0 mesh, node 1 Armature
    nodes = [{"name": "Body", "mesh": 0, "skin": 0}, {"name": "Armature", "children": [node_of["root"]]}]
    glob = {}
    for n, par, t, r in JOINTS:
        q = euler(*r)
        node = {"name": n, "translation": list(map(float, t)), "rotation": q}
        if as_matrices:
            m = _trs_matrix(t, q)
            node = {"name": n, "matrix": m}
        kids = [node_of[c[0]] for c in JOINTS if c[1] == n]
        if kids:
            node["children"] = kids
        nodes.append(node)
        glob[n] = _mul(glob[par], _trs(t, q)) if par else _trs(t, q)
    # one box around each joint, skinned to it
    P, N, J, W, I = [], [], [], [], []
    for ji, n in enumerate(names):
        c = (glob[n][0][3], glob[n][1][3], glob[n][2][3])
        for k, (nx, corners) in enumerate(_box()):
            base = len(P)
            for sx, sy, sz in corners:
                P.append((c[0] + sx * 0.08, c[1] + sy * 0.08, c[2] + sz * 0.08))
                N.append(nx)
                J.append((ji, 0, 0, 0))
                W.append((1.0, 0.0, 0.0, 0.0))
            I += [base, base + 1, base + 2, base, base + 2, base + 3]
    pos = w.add(b"".join(struct.pack("<3f", *p) for p in P), 5126, "VEC3", len(P), 34962,
                [min(p[i] for p in P) for i in range(3)], [max(p[i] for p in P) for i in range(3)])
    nor = w.add(b"".join(struct.pack("<3f", *p) for p in N), 5126, "VEC3", len(N), 34962)
    jnt = w.add(b"".join(struct.pack("<4H", *p) for p in J), 5123, "VEC4", len(J), 34962)
    wei = w.add(b"".join(struct.pack("<4f", *p) for p in W), 5126, "VEC4", len(W), 34962)
    idx = w.add(struct.pack(f"<{len(I)}H", *I), 5123, "SCALAR", len(I), 34963)
    ibm = w.add(b"".join(struct.pack("<16f", *[_inv(glob[n])[r][c] for c in range(4) for r in range(4)]) for n in names), 5126, "MAT4", len(names))

    def rot_track(name, deg_of_u, dur, keys=None):
        k = keys or max(2, int(dur * FPS_KEYS))
        ts = [dur * i / (k - 1) for i in range(k)]
        vals = [c for t in ts for c in arm_rot(name, deg_of_u(t / dur))]
        return ts, vals

    def anim(name, channels):
        samplers, chs = [], []
        for node_name, path, ts, vals, interp in channels:
            comps = {"rotation": "VEC4", "translation": "VEC3", "scale": "VEC3"}[path]
            n = {"VEC4": 4, "VEC3": 3}[comps]
            inp = w.add(w.floats(ts), 5126, "SCALAR", len(ts), None, [ts[0]], [ts[-1]])
            out = w.add(w.floats(vals), 5126, comps, len(vals) // n)
            samplers.append({"input": inp, "output": out, "interpolation": interp})
            chs.append({"sampler": len(samplers) - 1, "target": {"node": node_of[node_name], "path": path}})
        return {"name": name, "samplers": samplers, "channels": chs}

    swing = lambda amp: (lambda u: amp * math.sin(2 * math.pi * u))  # noqa: E731
    anims = []
    t, v = rot_track("arm_r", swing(ARM_SWING_DEG), 2.0)
    anims.append(anim("Idle", [("arm_r", "rotation", t, v, "LINEAR")]))
    for nm, amp in (("WalkF", 40.0), ("WalkL", 20.0), ("WalkR", -20.0), ("WalkB", -40.0)):
        t, v = rot_track("arm_r", swing(amp), 1.0)
        t2, v2 = rot_track("arm_l", swing(-amp), 1.0)
        anims.append(anim(nm, [("arm_r", "rotation", t, v, "LINEAR"), ("arm_l", "rotation", t2, v2, "LINEAR")]))
    t, v = rot_track("arm_r", swing(70.0), 0.5)
    anims.append(anim("Run", [("arm_r", "rotation", t, v, "LINEAR")]))
    # a step animation: three held poses
    anims.append(anim("Fall", [("arm_r", "rotation", [0.0, 0.5, 1.0], arm_rot("arm_r", 0) + arm_rot("arm_r", 45) + arm_rot("arm_r", 90), "STEP")]))
    # cubic spline: down and back, 1.5 s, zero tangents at both ends
    q0, q1 = arm_rot("arm_r", 0.0), arm_rot("arm_r", -90.0)
    zero = [0.0] * 4
    anims.append(anim("Slam", [("arm_r", "rotation", [0.0, 0.75, 1.5], zero + q0 + zero + zero + q1 + zero + zero + q0 + zero, "CUBICSPLINE")]))
    # root motion: the root moves 2 m forward over 2 s while the arm stays
    k = 61
    ts = [2.0 * i / (k - 1) for i in range(k)]
    anims.append(anim("Step", [("root", "translation", ts, [c for x in ts for c in (0.0, 0.0, x)], "LINEAR"),
                               ("arm_r", "rotation", [0.0, 2.0], arm_rot("arm_r", 15.0) + arm_rot("arm_r", 15.0), "LINEAR")]))
    # an arm offset to play as an additive layer, and its reference pose
    anims.append(anim("Flinch", [("arm_r", "rotation", [0.0, 0.25, 0.5], arm_rot("arm_r", 0) + arm_rot("arm_r", 25) + arm_rot("arm_r", 0), "LINEAR")]))
    anims.append(anim("Ref", [("arm_r", "rotation", [0.0, 0.5], arm_rot("arm_r", 0) + arm_rot("arm_r", 0), "LINEAR")]))
    # forty held poses: the arm at i degrees x 2, one second each, to tell nested states apart
    for i in range(40):
        q = arm_rot("arm_r", 2.0 * i)
        anims.append(anim(f"Pose{i:02d}", [("arm_r", "rotation", [0.0, 1.0], q + q, "LINEAR")]))
    doc = {"asset": {"version": "2.0", "generator": "kiln tools/animcook samples"}, "scene": 0,
           "scenes": [{"nodes": [0, 1]}], "nodes": nodes,
           "meshes": [{"name": "Body", "primitives": [{"attributes": {"POSITION": pos, "NORMAL": nor, "JOINTS_0": jnt, "WEIGHTS_0": wei}, "indices": idx}]}],
           "skins": [{"joints": [node_of[n] for n in names], "inverseBindMatrices": ibm, "skeleton": node_of["root"]}],
           "animations": anims, "bufferViews": w.views, "accessors": w.accessors,
           "buffers": [{"byteLength": len(w.blob)}]}
    return doc, bytes(w.blob)


def _box():
    return [((1, 0, 0), [(1, -1, 1), (1, -1, -1), (1, 1, -1), (1, 1, 1)]), ((-1, 0, 0), [(-1, -1, -1), (-1, -1, 1), (-1, 1, 1), (-1, 1, -1)]),
            ((0, 1, 0), [(-1, 1, 1), (1, 1, 1), (1, 1, -1), (-1, 1, -1)]), ((0, -1, 0), [(-1, -1, -1), (1, -1, -1), (1, -1, 1), (-1, -1, 1)]),
            ((0, 0, 1), [(-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]), ((0, 0, -1), [(1, -1, -1), (-1, -1, -1), (-1, 1, -1), (1, 1, -1)])]


def _trs(t, q):
    x, y, z, w = q
    r = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)], [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
         [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
    return [r[i] + [float(t[i])] for i in range(3)] + [[0.0, 0.0, 0.0, 1.0]]


def _trs_matrix(t, q):
    m = _trs(t, q)
    return [m[r][c] for c in range(4) for r in range(4)]


def _mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def _inv(m):
    r = [[m[j][i] for j in range(3)] for i in range(3)]
    t = [-sum(r[i][k] * m[k][3] for k in range(3)) for i in range(3)]
    return [r[i] + [t[i]] for i in range(3)] + [[0.0, 0.0, 0.0, 1.0]]


def write(path, as_gltf=False):
    doc, blob = build(as_matrices=as_gltf)
    if as_gltf:
        doc["buffers"][0]["uri"] = "data:application/octet-stream;base64," + base64.b64encode(blob).decode()
        with open(path, "w") as f:
            json.dump(doc, f)
        return
    js = json.dumps(doc, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    blob += b"\0" * (-len(blob) % 4)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(blob)))
        f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
        f.write(struct.pack("<II", len(blob), 0x004E4942) + blob)


if __name__ == "__main__":
    write(sys.argv[1], "--gltf" in sys.argv)
