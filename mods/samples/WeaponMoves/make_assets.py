#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes the sample's sources as glTF 2.0, with no game data: every number here is the sample's own.

    python3 mods/samples/WeaponMoves/make_assets.py        (writes assets/src/*.gltf; they are committed)

- assets/src/fighter.gltf: a small rig of its own (20 joints, no mesh) and three motions, authored here as keyed turns of its joints:
  `Slash` (a forehand, 1 s), `Cleave` (an overhead chop, 1.6 s) and `Guard` (the sword held across, a 6.65 s loop). assets/cook.json retargets
  them onto the player's skeleton, makes the second swing of the chain from `Slash` ("from", "mirror"), and puts the sword arm of each onto the
  game's clip of the state it is placed like ("blend"): the body moves as the game's, the arm as written here. Their lengths are the moves' own
  (`length` in blocks/weapon.json); the guard lasts as long as the game's block loop.
- assets/src/blade.gltf: the sword with moving parts, skinned to 3 joints (grip, core, tip), and its loop `Spin` (2 s): the crystal in the blade
  turns and the tip sways. It is the held model of the look's `animated`.
- assets/src/blade_rigid.gltf: the same sword in one piece, for the icon, the dropped item and the collection.

Space: glTF's, metres, facing +Z with y up, so the rig's right side is -X. A weapon model has +Y up the blade and its origin at the grip; its UVs
point into regions of the game's sword texture (steel, dark metal, leather; the regions tools/modelcook/samples/cube_sword.py uses), so the cook can
draw it with the game's own sword material, read from the installed game.
"""
import base64
import json
import math
import struct
from pathlib import Path

HERE = Path(__file__).resolve().parent
FPS_KEYS = 30

STEEL = (0.10, 0.40, 0.50, 0.85)    # u0, v0, u1, v1 in the sword texture
DARK = (0.06, 0.03, 0.44, 0.14)
LEATHER = (0.72, 0.26, 0.94, 0.50)

# ---- the rig: name, parent, translation from the parent (rest rotations are identity: each bone points where its child is) ----
# An A-pose: the arms hang 45 degrees out, as the player's skeleton rests.
S45 = math.sqrt(0.5)
RIG = [
    ("root", None, (0.0, 0.0, 0.0)),
    ("hips", "root", (0.0, 0.95, 0.0)),
    ("spine", "hips", (0.0, 0.12, 0.0)),
    ("chest", "spine", (0.0, 0.30, 0.0)),
    ("neck", "chest", (0.0, 0.15, 0.0)),
    ("head", "neck", (0.0, 0.14, 0.0)),
    ("clavicle_l", "chest", (0.02, 0.10, 0.0)),
    ("arm_l", "clavicle_l", (0.16, -0.03, 0.0)),
    ("forearm_l", "arm_l", (0.26 * S45, -0.26 * S45, 0.0)),
    ("hand_l", "forearm_l", (0.26 * S45, -0.26 * S45, 0.0)),
    ("clavicle_r", "chest", (-0.02, 0.10, 0.0)),
    ("arm_r", "clavicle_r", (-0.16, -0.03, 0.0)),
    ("forearm_r", "arm_r", (-0.26 * S45, -0.26 * S45, 0.0)),
    ("hand_r", "forearm_r", (-0.26 * S45, -0.26 * S45, 0.0)),
    ("thigh_l", "hips", (0.10, -0.03, 0.0)),
    ("shin_l", "thigh_l", (0.0, -0.43, 0.0)),
    ("foot_l", "shin_l", (0.0, -0.42, 0.0)),
    ("thigh_r", "hips", (-0.10, -0.03, 0.0)),
    ("shin_r", "thigh_r", (0.0, -0.43, 0.0)),
    ("foot_r", "shin_r", (0.0, -0.42, 0.0)),
]


def euler(x, y, z):
    """Degrees about x, then y, then z (the parent's axes)."""
    def q(ax, deg):
        h = math.radians(deg) / 2
        v = [0.0, 0.0, 0.0, math.cos(h)]
        v[ax] = math.sin(h)
        return v
    return qmul(q(2, z), qmul(q(1, y), q(0, x)))


def qmul(a, b):
    return [a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1], a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3], a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]]


def ease(u):
    return u * u * (3 - 2 * u)


# A motion: its length, and keys (fraction of the length -> {joint: (x, y, z) degrees}); a joint missing from a key is at rest there.
# Arms: about x lifts forward (negative), about z swings out to the side (the right arm out is negative, the left positive).
# The ready pose is the same on both sides, so the mirrored second swing starts and ends where the first does.
READY = {"arm_r": (-25, 0, 10), "forearm_r": (-30, 0, 0), "arm_l": (-25, 0, -10), "forearm_l": (-30, 0, 0)}
GUARD_A = {"chest": (0, 10, 0), "arm_r": (-90, 0, 30), "forearm_r": (-60, 0, 0), "arm_l": (-60, 0, -20), "forearm_l": (-70, 0, 0)}
GUARD_B = {"chest": (3, 10, 0), "arm_r": (-93, 0, 32), "forearm_r": (-64, 0, 0), "arm_l": (-62, 0, -20), "forearm_l": (-72, 0, 0)}
MOTIONS = {
    # the forehand: the sword comes from high on the right across to the left. The left arm carries the backhand: from high across the body
    # out to the left side. The cook mirrors this clip for the second swing, so there the right arm (with the sword) plays the backhand.
    # Only the sword arm goes onto the game's clip, so in the first swing the left arm is the game's (the shield)
    "Slash": (1.0, [
        (0.00, READY),
        (0.30, {"chest": (0, -35, 0), "spine": (0, -10, 0), "arm_r": (-120, 0, -50), "forearm_r": (-40, 0, 0), "arm_l": (-110, 0, -55), "forearm_l": (-60, 0, 0)}),
        (0.42, {"chest": (0, 30, 0), "spine": (0, 10, 0), "arm_r": (-80, 0, 40), "forearm_r": (-10, 0, 0), "arm_l": (-90, 0, 0), "forearm_l": (-10, 0, 0)}),
        (0.70, {"chest": (0, 40, 0), "spine": (0, 12, 0), "arm_r": (-50, 0, 60), "forearm_r": (-20, 0, 0), "arm_l": (-40, 0, 75), "forearm_l": (-15, 0, 0)}),
        (1.00, READY)]),
    # the heavy: both hands up over the head, then down; the body leans into it and the knees give
    "Cleave": (1.6, [
        (0.00, READY),
        (0.38, {"chest": (-15, 0, 0), "arm_r": (-170, 0, 10), "forearm_r": (-30, 0, 0), "arm_l": (-170, 0, -10), "forearm_l": (-30, 0, 0)}),
        (0.56, {"chest": (30, 0, 0), "spine": (10, 0, 0), "arm_r": (-60, 0, -10), "forearm_r": (-5, 0, 0), "arm_l": (-60, 0, 10), "forearm_l": (-5, 0, 0),
                "thigh_l": (-25, 0, 0), "shin_l": (40, 0, 0), "thigh_r": (-25, 0, 0), "shin_r": (40, 0, 0)}),
        (0.80, {"chest": (25, 0, 0), "spine": (8, 0, 0), "arm_r": (-55, 0, -10), "forearm_r": (-5, 0, 0), "arm_l": (-55, 0, 10), "forearm_l": (-5, 0, 0),
                "thigh_l": (-20, 0, 0), "shin_l": (32, 0, 0), "thigh_r": (-20, 0, 0), "shin_r": (32, 0, 0)}),
        (1.00, READY)]),
    # the guard: the sword held across in front, the arm giving a little five times; it ends where it starts, so it loops. It lasts as long as
    # the game's block loop it goes onto (Block_FullBody, 6.65 s), so the cook does not stretch it
    "Guard": (6.65, [(i / 10, GUARD_A if i % 2 == 0 else GUARD_B) for i in range(11)]),
}


class Writer:
    def __init__(self):
        self.blob = bytearray()
        self.views, self.accessors = [], []

    def add(self, fmt, rows, comp, typ, target=None, minmax=False):
        data = b"".join(struct.pack(fmt, *r) for r in rows)
        while len(self.blob) % 4:
            self.blob.append(0)
        v = {"buffer": 0, "byteOffset": len(self.blob), "byteLength": len(data)}
        if target:
            v["target"] = target
        self.views.append(v)
        self.blob.extend(data)
        a = {"bufferView": len(self.views) - 1, "componentType": comp, "count": len(rows), "type": typ}
        if minmax:
            a["min"] = [min(r[i] for r in rows) for i in range(len(rows[0]))]
            a["max"] = [max(r[i] for r in rows) for i in range(len(rows[0]))]
        self.accessors.append(a)
        return len(self.accessors) - 1

    def animation(self, name, tracks):
        """tracks: [(node, path, times, values)], LINEAR."""
        samplers, channels = [], []
        for node, path, times, values in tracks:
            inp = self.add("<f", [(t,) for t in times], 5126, "SCALAR", minmax=True)
            out = self.add("<4f" if path == "rotation" else "<3f", values, 5126, "VEC4" if path == "rotation" else "VEC3")
            samplers.append({"input": inp, "output": out, "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": node, "path": path}})
        return {"name": name, "samplers": samplers, "channels": channels}

    def save(self, path, doc):
        doc["asset"] = {"version": "2.0", "generator": "kiln mods/samples/WeaponMoves/make_assets.py"}
        doc["bufferViews"], doc["accessors"] = self.views, self.accessors
        doc["buffers"] = [{"byteLength": len(self.blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(self.blob)).decode()}]
        Path(path).write_text(json.dumps(doc, indent=None, separators=(",", ":")) + "\n")


def fighter(path):
    w = Writer()
    index = {n: i for i, (n, _, _) in enumerate(RIG)}
    nodes = []
    for n, parent, t in RIG:
        node = {"name": n, "translation": list(t)}
        kids = [index[c] for c, p, _ in RIG if p == n]
        if kids:
            node["children"] = kids
        nodes.append(node)
    anims = []
    for name, (length, keys) in MOTIONS.items():
        moved = sorted({j for _, pose in keys for j in pose}, key=index.get)
        n = round(length * FPS_KEYS)
        times = [length * i / n for i in range(n + 1)]
        tracks = []
        for j in moved:
            vals = []
            for t in times:
                u = t / length
                k = max(i for i, (at, _) in enumerate(keys) if at <= u + 1e-9)
                k = min(k, len(keys) - 2)
                (a0, p0), (a1, p1) = keys[k], keys[k + 1]
                s = ease(min(1.0, max(0.0, (u - a0) / (a1 - a0))))
                e0, e1 = p0.get(j, (0, 0, 0)), p1.get(j, (0, 0, 0))
                vals.append(euler(*[x + (y - x) * s for x, y in zip(e0, e1)]))
            tracks.append((index[j], "rotation", times, vals))
        anims.append(w.animation(name, tracks))
    w.save(path, {"scene": 0, "scenes": [{"nodes": [0]}], "nodes": nodes, "animations": anims})


FACES = [((1, 0, 0), [(1, -1, 1), (1, -1, -1), (1, 1, -1), (1, 1, 1)]), ((-1, 0, 0), [(-1, -1, -1), (-1, -1, 1), (-1, 1, 1), (-1, 1, -1)]),
         ((0, 1, 0), [(-1, 1, 1), (1, 1, 1), (1, 1, -1), (-1, 1, -1)]), ((0, -1, 0), [(-1, -1, -1), (1, -1, -1), (1, -1, 1), (-1, -1, 1)]),
         ((0, 0, 1), [(-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]), ((0, 0, -1), [(1, -1, -1), (-1, -1, -1), (-1, 1, -1), (1, 1, -1)])]

# the sword's boxes: centre, half size, UV region, the joint it moves with. +Y up the blade, origin at the grip, the guard along z
BLADE = [
    ((0.0, -0.08, 0.0), (0.020, 0.090, 0.020), LEATHER, "grip"),     # grip
    ((0.0, -0.19, 0.0), (0.030, 0.025, 0.030), DARK, "grip"),        # pommel
    ((0.0, 0.03, 0.0), (0.025, 0.020, 0.130), DARK, "grip"),         # guard
    ((0.0, 0.25, 0.0), (0.012, 0.200, 0.040), STEEL, "grip"),        # lower blade
    ((0.0, 0.50, 0.0), (0.035, 0.035, 0.035), DARK, "core"),         # the crystal that turns
    ((0.0, 0.76, 0.0), (0.010, 0.190, 0.034), STEEL, "tip"),         # upper blade, sways
]
BLADE_JOINTS = [("grip", None, (0.0, 0.0, 0.0)), ("core", "grip", (0.0, 0.50, 0.0)), ("tip", "grip", (0.0, 0.57, 0.0))]


def blade(path, skinned):
    w = Writer()
    P, N, UV, J, W, I = [], [], [], [], [], []
    names = [j[0] for j in BLADE_JOINTS]
    for centre, half, (u0, v0, u1, v1), joint in BLADE:
        for nrm, corners in FACES:
            base = len(P)
            for k, (sx, sy, sz) in enumerate(corners):
                P.append((centre[0] + sx * half[0], centre[1] + sy * half[1], centre[2] + sz * half[2]))
                N.append(nrm)
                UV.append((u0 if k in (0, 3) else u1, v1 if k in (0, 1) else v0))
                J.append((names.index(joint), 0, 0, 0))
                W.append((1.0, 0.0, 0.0, 0.0))
            I += [base, base + 1, base + 2, base, base + 2, base + 3]
    attrs = {"POSITION": w.add("<3f", P, 5126, "VEC3", 34962, minmax=True), "NORMAL": w.add("<3f", N, 5126, "VEC3", 34962),
             "TEXCOORD_0": w.add("<2f", UV, 5126, "VEC2", 34962)}
    if skinned:
        attrs["JOINTS_0"] = w.add("<4H", J, 5123, "VEC4", 34962)
        attrs["WEIGHTS_0"] = w.add("<4f", W, 5126, "VEC4", 34962)
    idx = w.add("<H", [(i,) for i in I], 5123, "SCALAR", 34963)
    doc = {"scene": 0, "materials": [{"name": "sword"}],
           "meshes": [{"name": "blade", "primitives": [{"attributes": attrs, "indices": idx, "material": 0}]}]}
    if not skinned:
        doc["nodes"] = [{"name": "blade", "mesh": 0}]
        doc["scenes"] = [{"nodes": [0]}]
        w.save(path, doc)
        return
    # node 0 the mesh, nodes 1.. the joints; the joints sit at their rest offsets, so each inverse bind is a plain translation
    nodes = [{"name": "blade", "mesh": 0, "skin": 0}]
    glob = {}
    for n, parent, t in BLADE_JOINTS:
        glob[n] = tuple(a + b for a, b in zip(glob[parent], t)) if parent else t
        node = {"name": n, "translation": list(t)}
        kids = [1 + names.index(c) for c, p, _ in BLADE_JOINTS if p == n]
        if kids:
            node["children"] = kids
        nodes.append(node)
    ibm = w.add("<16f", [(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -glob[n][0], -glob[n][1], -glob[n][2], 1) for n in names], 5126, "MAT4")
    doc["nodes"] = nodes
    doc["scenes"] = [{"nodes": [0, 1]}]
    doc["skins"] = [{"joints": [1 + i for i in range(len(names))], "inverseBindMatrices": ibm, "skeleton": 1}]
    # Spin: the crystal turns once about the blade every 2 s, the tip sways 8 degrees to each side; the last key equals the first
    n = 2 * FPS_KEYS
    times = [2.0 * i / n for i in range(n + 1)]
    spin = [euler(0, 360.0 * i / n, 0) for i in range(n + 1)]
    spin[-1] = spin[0]
    sway = [euler(0, 0, 8.0 * math.sin(2 * math.pi * i / n)) for i in range(n + 1)]
    doc["animations"] = [w.animation("Spin", [(1 + names.index("core"), "rotation", times, spin), (1 + names.index("tip"), "rotation", times, sway)])]
    w.save(path, doc)


if __name__ == "__main__":
    src = HERE / "assets" / "src"
    src.mkdir(parents=True, exist_ok=True)
    fighter(src / "fighter.gltf")
    blade(src / "blade.gltf", skinned=True)
    blade(src / "blade_rigid.gltf", skinned=False)
    print("wrote", ", ".join(str(p.relative_to(HERE)) for p in sorted(src.glob("*.gltf"))))
