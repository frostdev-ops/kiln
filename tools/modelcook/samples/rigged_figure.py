#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes a small rigged figure as glTF 2.0 (.glb), no game data: a body of tapered tubes with smooth weights, 15 joints, a club in its right hand, and the animations a
creature needs (Idle, Walk, Run, Attack, Dead, Flinch), plus Bend and Hold, large poses for checks. Facing +Z, y up, metres, so its right side is -X in the file and +X in the game. The club is a mesh with no skin, parented to the hand: it tests that case
and it tells a mirrored body from a correct one.

    python3 tools/modelcook/samples/rigged_figure.py OUT.glb [--lod1]

--lod1 writes a coarser body with the same skeleton. Used by tools/modelcook/tests and mods/tests/skincheck.
"""
import json
import math
import struct
import sys

# name, parent, rest translation, rest rotation about z in degrees (the limbs hang at a small angle, so inverse binds aren't plain translations)
JOINTS = [("root", None, (0, 0, 0), 0),
          ("pelvis", "root", (0, 0.95, 0), 0),
          ("spine", "pelvis", (0, 0.22, 0), 0),
          ("chest", "spine", (0, 0.24, 0), 0),
          ("head", "chest", (0, 0.32, 0), 0),
          ("arm_r", "chest", (-0.22, 0.18, 0), -8),
          ("forearm_r", "arm_r", (0, -0.30, 0), 0),
          ("hand_r", "forearm_r", (0, -0.28, 0), 0),
          ("arm_l", "chest", (0.22, 0.18, 0), 8),
          ("forearm_l", "arm_l", (0, -0.30, 0), 0),
          ("hand_l", "forearm_l", (0, -0.28, 0), 0),
          ("leg_r", "pelvis", (-0.11, -0.04, 0), 0),
          ("shin_r", "leg_r", (0, -0.45, 0), 0),
          ("leg_l", "pelvis", (0.11, -0.04, 0), 0),
          ("shin_l", "leg_l", (0, -0.45, 0), 0)]
NAMES = [j[0] for j in JOINTS]
CLUB_LENGTH = 0.55


def axis_quat(axis, deg):
    h = math.radians(deg) / 2
    q = [0.0, 0.0, 0.0, math.cos(h)]
    q["xyz".index(axis)] = math.sin(h)
    return q


def qmul(a, b):
    return [a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1], a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3], a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]]


def rest_rotation(name):
    return axis_quat("z", next(j[3] for j in JOINTS if j[0] == name))


def _rot(q, v):
    x, y, z, w = q
    tx, ty, tz = 2 * (y * v[2] - z * v[1]), 2 * (z * v[0] - x * v[2]), 2 * (x * v[1] - y * v[0])
    return (v[0] + w * tx + y * tz - z * ty, v[1] + w * ty + z * tx - x * tz, v[2] + w * tz + x * ty - y * tx)


def world_rest():
    """name -> (position, rotation) of each joint at rest."""
    out = {}
    for name, parent, t, _ in JOINTS:
        q = rest_rotation(name)
        if parent is None:
            out[name] = (tuple(float(c) for c in t), q)
        else:
            pp, pq = out[parent]
            r = _rot(pq, t)
            out[name] = ((pp[0] + r[0], pp[1] + r[1], pp[2] + r[2]), qmul(pq, q))
    return out


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _unit(v):
    n = math.sqrt(sum(c * c for c in v)) or 1.0
    return tuple(c / n for c in v)


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


class Mesh:
    def __init__(self):
        self.P, self.N, self.UV, self.W, self.I = [], [], [], [], []

    def tube(self, a, b, ra, rb, weights, sides, rings, v0=0.0, v1=1.0):
        """A closed tube from a to b. weights(u) -> [(joint, weight)] along it."""
        d = _unit(_sub(b, a))
        side = _unit(_cross(d, (0, 0, 1) if abs(d[2]) < 0.9 else (1, 0, 0)))
        fwd = _cross(side, d)
        first = len(self.P)
        rows = [(0.0, 0.002)] + [(i / rings, ra + (rb - ra) * i / rings) for i in range(rings + 1)] + [(1.0, 0.002)]
        for ri, (u, r) in enumerate(rows):
            c = tuple(a[k] + (b[k] - a[k]) * u for k in range(3))
            for s in range(sides):
                th = 2 * math.pi * s / sides
                out = tuple(math.cos(th) * side[k] + math.sin(th) * fwd[k] for k in range(3))
                self.P.append(tuple(c[k] + r * out[k] for k in range(3)))
                self.N.append(tuple(-x for x in d) if ri == 0 else d if ri == len(rows) - 1 else out)
                self.UV.append((s / sides, v0 + (v1 - v0) * u))
                self.W.append(weights(u))
        for row in range(len(rows) - 1):
            for s in range(sides):
                n = (s + 1) % sides
                p, q, r2, t = first + row * sides + s, first + row * sides + n, first + (row + 1) * sides + n, first + (row + 1) * sides + s
                self.I += [p, t, r2, p, r2, q]


def _blend(j, up=None, down=None, span=0.25):
    """A bone's weights: all `j` in the middle, shared half and half with its neighbour at each end."""
    def w(u):
        if up and u < span:
            b = 0.5 * (1 - u / span)
            return [(j, 1 - b), (up, b)]
        if down and u > 1 - span:
            b = 0.5 * (u - (1 - span)) / span
            return [(j, 1 - b), (down, b)]
        return [(j, 1.0)]
    return w


def _torso(u):
    """Three joints share the trunk."""
    ws = {"pelvis": max(0.0, 1 - u * 2.6), "spine": max(0.0, 1 - abs(u - 0.38) * 3.2), "chest": max(0.0, (u - 0.2) * 1.6), "head": 0.08 if u > 0.9 else 0.0}
    t = sum(ws.values())
    return [(k, v / t) for k, v in ws.items() if v > 0]


def body(sides=10, rings=4):
    w = world_rest()
    at = lambda n: w[n][0]
    m = Mesh()
    m.tube((0, at("pelvis")[1] - 0.10, 0), (0, at("head")[1], 0), 0.20, 0.17, _torso, sides, rings * 3, 0.0, 0.5)
    m.tube(at("head"), (0, at("head")[1] + 0.26, 0.01), 0.10, 0.12, lambda u: [("head", 1.0)], sides, 2, 0.5, 0.7)
    for s in "rl":
        arm, fore, hand = (at(f"{n}_{s}") for n in ("arm", "forearm", "hand"))
        tip = tuple(hand[k] + (hand[k] - fore[k]) * 0.5 for k in range(3))
        m.tube(arm, fore, 0.075, 0.06, _blend(f"arm_{s}", "chest", f"forearm_{s}"), sides, rings, 0.7, 0.8)
        m.tube(fore, hand, 0.06, 0.05, _blend(f"forearm_{s}", f"arm_{s}", f"hand_{s}"), sides, rings, 0.8, 0.9)
        m.tube(hand, tip, 0.05, 0.035, lambda u, h=f"hand_{s}": [(h, 1.0)], sides, 1, 0.9, 1.0)
        leg, shin = at(f"leg_{s}"), at(f"shin_{s}")
        foot = (shin[0], 0.02, shin[2] + 0.05)
        m.tube(leg, shin, 0.095, 0.07, _blend(f"leg_{s}", "pelvis", f"shin_{s}"), sides, rings, 0.0, 0.3)
        m.tube(shin, foot, 0.07, 0.05, _blend(f"shin_{s}", f"leg_{s}"), sides, rings, 0.3, 0.5)
    return m


def club():
    """In the hand's own space: a thick bar pointing along the hand's -y, then forward."""
    m = Mesh()
    m.tube((0, -0.10, 0), (0, -0.10, CLUB_LENGTH), 0.025, 0.06, lambda u: [("hand_r", 1.0)], 8, 2)
    return m


# ---- animations: per-joint rotations over time, the rest rotation included ----
def _swing(a, b, u):
    return a + (b - a) * (0.5 - 0.5 * math.cos(2 * math.pi * u))


def _pose_idle(u):
    return {"chest": ("x", _swing(0, 4, u)), "arm_r": ("x", _swing(0, 3, u)), "arm_l": ("x", _swing(0, -3, u))}


def _pose_walk(u, amp=30.0):
    s = math.sin(2 * math.pi * u)
    return {"leg_r": ("x", -amp * s), "leg_l": ("x", amp * s), "shin_r": ("x", amp * max(0.0, s)), "shin_l": ("x", amp * max(0.0, -s)), "arm_r": ("x", amp * s * 0.7),
            "arm_l": ("x", -amp * s * 0.7)}


def _pose_attack(u):
    raised = -150 * min(1.0, u / 0.4) if u < 0.4 else -150 + 170 * min(1.0, (u - 0.4) / 0.15) if u < 0.55 else 20 * (1 - (u - 0.55) / 0.45)
    return {"arm_r": ("x", raised), "forearm_r": ("x", raised * 0.2), "chest": ("y", raised * 0.1)}


def _pose_dead(u):
    return {"pelvis": ("x", -90 * min(1.0, u * 1.5)), "arm_r": ("z", -60 * u), "arm_l": ("z", 60 * u)}


def _pose_bend(u):
    """A large pose for checks: bends at every level of the skeleton."""
    return {"spine": ("x", 35 * u), "chest": ("z", 20 * u), "head": ("y", 50 * u), "arm_r": ("x", -90 * u), "forearm_r": ("x", -60 * u), "hand_r": ("z", 40 * u), "arm_l": ("z", 70 * u),
            "leg_r": ("x", -50 * u), "shin_r": ("x", 70 * u), "leg_l": ("x", 30 * u)}


def _pose_flinch(u):
    return {"chest": ("x", 25 * math.sin(math.pi * u)), "head": ("x", -15 * math.sin(math.pi * u))}


ANIMATIONS = {"Idle": (_pose_idle, 2.0), "Walk": (_pose_walk, 1.0), "Run": (lambda u: _pose_walk(u, 55.0), 0.6), "Attack": (_pose_attack, 1.2), "Dead": (_pose_dead, 1.2), "Bend": (_pose_bend, 1.0),
              "Hold": (lambda u: _pose_bend(1.0), 1.0), "Flinch": (_pose_flinch, 0.5), "Ref": (lambda u: {"chest": ("x", 0.0)}, 0.1)}
PELVIS_DROP = {"Dead": lambda u: -0.78 * min(1.0, u * 1.5)}  # the body falls to the ground as it turns over
KEYS_PER_SECOND = 30


def pose_rotation(name, pose):
    """The joint's local rotation in a pose: its rest rotation, then the pose's turn."""
    r = rest_rotation(name)
    if name in pose:
        return qmul(r, axis_quat(*pose[name]))
    return r


class Writer:
    def __init__(self):
        self.blob, self.views, self.accessors = bytearray(), [], []

    def add(self, data, comp, typ, count, target=None, mn=None, mx=None):
        while len(self.blob) % 4:
            self.blob.append(0)
        v = {"buffer": 0, "byteOffset": len(self.blob), "byteLength": len(data)}
        if target:
            v["target"] = target
        self.views.append(v)
        self.blob += data
        a = {"bufferView": len(self.views) - 1, "componentType": comp, "count": count, "type": typ}
        if mn is not None:
            a["min"], a["max"] = mn, mx
        self.accessors.append(a)
        return len(self.accessors) - 1

    def primitive(self, m, material, skinned):
        P = m.P
        at = {"POSITION": self.add(b"".join(struct.pack("<3f", *p) for p in P), 5126, "VEC3", len(P), 34962,
                                   [min(p[i] for p in P) for i in range(3)], [max(p[i] for p in P) for i in range(3)]),
              "NORMAL": self.add(b"".join(struct.pack("<3f", *n) for n in m.N), 5126, "VEC3", len(P), 34962),
              "TEXCOORD_0": self.add(b"".join(struct.pack("<2f", *uv) for uv in m.UV), 5126, "VEC2", len(P), 34962)}
        if skinned:
            js, ws = bytearray(), bytearray()
            for inf in m.W:
                slots = (inf + [("root", 0.0)] * 4)[:4]
                js += struct.pack("<4B", *(NAMES.index(j) for j, _ in slots))
                ws += struct.pack("<4f", *(w for _, w in slots))
            at["JOINTS_0"] = self.add(bytes(js), 5121, "VEC4", len(P), 34962)
            at["WEIGHTS_0"] = self.add(bytes(ws), 5126, "VEC4", len(P), 34962)
        idx = self.add(struct.pack(f"<{len(m.I)}H", *m.I), 5123, "SCALAR", len(m.I), 34963)
        return {"attributes": at, "indices": idx, "material": material}


def _inverse_bind():
    """Column-major inverse of each joint's rest transform (rotation + translation only here)."""
    out = bytearray()
    for name, (p, q) in world_rest().items():
        x, y, z, w = -q[0], -q[1], -q[2], q[3]
        r = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
             [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
             [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
        t = [-sum(r[i][k] * p[k] for k in range(3)) for i in range(3)]
        out += struct.pack("<16f", *[r[0][0], r[1][0], r[2][0], 0, r[0][1], r[1][1], r[2][1], 0, r[0][2], r[1][2], r[2][2], 0, t[0], t[1], t[2], 1])
    return bytes(out)


def build(lod1=False):
    wr = Writer()
    b = body(sides=5, rings=2) if lod1 else body()
    prims = [wr.primitive(b, 0, True)]
    club_prim = wr.primitive(club(), 1, False)
    nodes = [{"name": "Armature", "children": [1]}]
    idx = {n: i + 1 for i, n in enumerate(NAMES)}
    for name, parent, t, _ in JOINTS:
        node = {"name": name, "translation": [float(c) for c in t], "rotation": rest_rotation(name)}
        nodes.append(node)
    for name, parent, _, _ in JOINTS:
        if parent:
            nodes[idx[parent]].setdefault("children", []).append(idx[name])
    club_node = len(nodes)
    nodes.append({"name": "Club", "mesh": 1})
    nodes[idx["hand_r"]].setdefault("children", []).append(club_node)
    # glTF ignores a skinned mesh node's own transform; this one has one, and a reader that applied it would move the body
    body_node = len(nodes)
    nodes.append({"name": "Body", "mesh": 0, "skin": 0, "translation": [5.0, 0.0, 0.0], "scale": [2.0, 2.0, 2.0]})
    skin = {"name": "Figure", "joints": [idx[n] for n in NAMES], "skeleton": idx["root"],
            "inverseBindMatrices": wr.add(_inverse_bind(), 5126, "MAT4", len(NAMES))}
    anims = []
    for aname, (pose, length) in ANIMATIONS.items():
        n = max(2, int(length * KEYS_PER_SECOND) + 1)
        times = [length * i / (n - 1) for i in range(n)]
        ti = wr.add(struct.pack(f"<{n}f", *times), 5126, "SCALAR", n, None, [0.0], [length])
        samplers, channels = [], []
        for jname in NAMES:
            if not any(jname in pose(i / (n - 1)) for i in range(n)):
                continue
            vals = b"".join(struct.pack("<4f", *pose_rotation(jname, pose(i / (n - 1)))) for i in range(n))
            samplers.append({"input": ti, "output": wr.add(vals, 5126, "VEC4", n), "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": idx[jname], "path": "rotation"}})
        if aname in PELVIS_DROP:
            rest = next(j[2] for j in JOINTS if j[0] == "pelvis")
            vals = b"".join(struct.pack("<3f", rest[0], rest[1] + PELVIS_DROP[aname](i / (n - 1)), rest[2]) for i in range(n))
            samplers.append({"input": ti, "output": wr.add(vals, 5126, "VEC3", n), "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": idx["pelvis"], "path": "translation"}})
        anims.append({"name": aname, "samplers": samplers, "channels": channels})
    doc = {"asset": {"version": "2.0", "generator": "kiln rigged_figure.py"}, "scene": 0, "scenes": [{"nodes": [body_node, 0]}], "nodes": nodes,
           "meshes": [{"name": "Body", "primitives": prims}, {"name": "Club", "primitives": [club_prim]}],
           "materials": [{"name": "body"}, {"name": "club"}], "skins": [skin], "animations": anims,
           "buffers": [{"byteLength": len(wr.blob)}], "bufferViews": wr.views, "accessors": wr.accessors}
    return doc, bytes(wr.blob)


def write(path, lod1=False):
    doc, blob = build(lod1)
    js = json.dumps(doc, separators=(",", ":")).encode()
    js += b" " * ((4 - len(js) % 4) % 4)
    blob += b"\0" * ((4 - len(blob) % 4) % 4)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(blob)))
        f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
        f.write(struct.pack("<II", len(blob), 0x004E4942) + blob)
    return doc


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    write(sys.argv[1], "--lod1" in sys.argv)
