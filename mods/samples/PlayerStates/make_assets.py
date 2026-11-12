#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes the sample's own motion as glTF 2.0, with no game data: assets/src/riposte.gltf (committed, shipped in the mod's zip).

    python3 mods/samples/PlayerStates/make_assets.py

A small rig of its own (20 joints, no mesh, an A-pose like the player's skeleton at rest) and three animations of its sword arm: `swing`,
`heavy` and `guard`. assets/cook.json retargets them onto the player's skeleton and puts the arm (upper arm, forearm, hand) onto the game's
own clip of the state each one is placed like: the body moves as the game moves it, the sword arm as written here.

Each animation lasts as long as the game clip it goes onto (the sword attack 1.667 s, the heavy attack 2.567 s, the block's loop 6.65 s) and
its blow lands where the game's sequence lands it (0.217 s, 0.983 s), so the hits and the hand-back keep the game's timing. Change a length
here and the cook warns that it stretches the arm to the game clip's length.

A real mod animates in Blender: on a rig like this one (retargeted, as here), or on the player's skeleton itself (`kiln skeleton player
player.gltf` writes it from your installed game; such a file holds game data and is not shipped).

Space: glTF's, metres, facing +Z with y up, so the rig's right side is -X.
"""
import base64
import json
import math
import struct
from pathlib import Path

HERE = Path(__file__).resolve().parent
FPS = 60  # one key per frame of the cook

# ---- the rig: name, parent, translation from the parent (rest rotations are identity: each bone points where its child is) ----
# An A-pose: the arms hang 45 degrees out, as the player's skeleton rests (the same rig as mods/samples/WeaponMoves/make_assets.py).
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
REST_DIR = (-S45, -S45, 0.0)  # where the right upper arm, forearm and hand point at rest

# ---- the motions ----
# A pose of the sword arm: where the upper arm points, where the forearm points (both in the body's axes: x left, y up, z forward), and the
# wrist: degrees the hand bends up (+) or down (-) from the line of the forearm.
READY = ((-0.35, -0.85, 0.40), (-0.10, -0.25, 0.96), 10)           # the sword held low in front, as between blows
MOTIONS = {
    # The swing: from low behind the right hip up across the body and over the left shoulder. The blow at 0.217 s (the sword attack's).
    "swing": (100 / FPS, False, [
        (0.000, READY),
        (0.110, ((-0.55, -0.75, -0.35), (-0.45, -0.85, -0.25), -35)),  # drawn back low behind the right hip
        (0.217, ((-0.10, -0.25, 0.96), (0.35, 0.10, 0.93), 0)),         # the blow: across the front at the waist
        (0.330, ((0.45, 0.45, 0.77), (0.60, 0.75, 0.28), 25)),          # up over the left shoulder
        (0.600, ((0.35, 0.30, 0.89), (0.45, 0.55, 0.70), 20)),          # held a moment
        (1.250, READY),
        (100 / FPS, READY)]),
    # The heavy: the arm goes up behind the head and chops down in front. The blow at 0.983 s (the heavy attack's).
    "heavy": (154 / FPS, False, [
        (0.000, READY),
        (0.450, ((-0.50, 0.40, 0.77), (-0.35, 0.85, 0.40), 10)),        # rising past the shoulder
        (0.800, ((-0.20, 0.95, -0.24), (-0.05, 0.35, -0.94), -40)),     # up behind the head, the sword down the back
        (0.983, ((-0.15, -0.05, 0.99), (-0.05, -0.55, 0.83), 15)),      # the blow: down in front
        (1.150, ((-0.20, -0.70, 0.69), (-0.05, -0.97, 0.25), 20)),      # through, low
        (1.700, ((-0.20, -0.70, 0.69), (-0.05, -0.95, 0.30), 15)),
        (2.250, READY),
        (154 / FPS, READY)]),
    # The guard: the sword arm up and out, the forearm across above the head, the blade held flat; a slow sway, five times in the loop.
    "guard": (399 / FPS, True, "sway"),
}
GUARD = ((-0.55, 0.78, 0.30), (0.95, 0.15, 0.27), -10)
GUARD_SWAY = ((-0.60, 0.72, 0.35), (0.93, 0.10, 0.35), -14)
SWAYS = 5


def qmul(a, b):
    return [a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1], a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3], a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]]


def qinv(q):
    return [-q[0], -q[1], -q[2], q[3]]


def qrot(q, v):
    return qmul(qmul(q, [v[0], v[1], v[2], 0.0]), qinv(q))[:3]


def norm(v):
    n = math.sqrt(sum(x * x for x in v))
    return [x / n for x in v]


def arc(a, b):
    """The shortest turn taking direction a to direction b."""
    a, b = norm(a), norm(b)
    d = sum(x * y for x, y in zip(a, b))
    c = [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
    q = [c[0], c[1], c[2], 1.0 + d]
    if q[3] < 1e-6:  # opposite: any axis across
        q = [0.0, 0.0, 1.0, 0.0]
    n = math.sqrt(sum(x * x for x in q))
    return [x / n for x in q]


def axis_angle(axis, deg):
    h = math.radians(deg) / 2
    axis = norm(axis)
    return [axis[0] * math.sin(h), axis[1] * math.sin(h), axis[2] * math.sin(h), math.cos(h)]


def locals_of(pose):
    """A pose -> the local rotations of arm_r, forearm_r and hand_r. Each bone only swings (no twist about its own length): the upper arm from
    rest to its direction, the forearm from the upper arm's line to its own, the hand bent about the elbow's axis."""
    up, fore, wrist = pose
    q_up = arc(REST_DIR, up)
    fore_local = qrot(qinv(q_up), norm(fore))
    q_fore = arc(REST_DIR, fore_local)
    f = norm(fore_local)
    if abs(sum(x * y for x, y in zip(f, REST_DIR))) > 0.999:
        bend_axis = [0.0, 0.0, 1.0]  # a straight elbow: bend the wrist about the body's forward axis
    else:  # the elbow's axis: across the upper arm's line and the forearm's
        bend_axis = norm([REST_DIR[1] * f[2] - REST_DIR[2] * f[1], REST_DIR[2] * f[0] - REST_DIR[0] * f[2], REST_DIR[0] * f[1] - REST_DIR[1] * f[0]])
    q_hand = axis_angle(bend_axis, -wrist)
    return q_up, q_fore, q_hand


def ease(u):
    return u * u * (3 - 2 * u)


def mix(p0, p1, s):
    """Two poses mixed: directions lerped and normalised, the wrist lerped."""
    return (norm([a + (b - a) * s for a, b in zip(p0[0], p1[0])]), norm([a + (b - a) * s for a, b in zip(p0[1], p1[1])]),
            p0[2] + (p1[2] - p0[2]) * s)


def pose_at(motion, t):
    length, _, keys = motion
    if keys == "sway":
        s = 0.5 - 0.5 * math.cos(2 * math.pi * SWAYS * t / length)
        return mix(GUARD, GUARD_SWAY, s)
    for (t0, p0), (t1, p1) in zip(keys, keys[1:]):
        if t0 <= t <= t1:
            return mix(p0, p1, ease((t - t0) / (t1 - t0)) if t1 > t0 else 1.0)
    return keys[-1][1]


class Writer:
    def __init__(self):
        self.blob = bytearray()
        self.views, self.accessors = [], []

    def add(self, fmt, rows, typ, minmax=False):
        data = b"".join(struct.pack(fmt, *r) for r in rows)
        while len(self.blob) % 4:
            self.blob.append(0)
        self.views.append({"buffer": 0, "byteOffset": len(self.blob), "byteLength": len(data)})
        self.blob.extend(data)
        a = {"bufferView": len(self.views) - 1, "componentType": 5126, "count": len(rows), "type": typ}
        if minmax:
            a["min"] = [min(r[i] for r in rows) for i in range(len(rows[0]))]
            a["max"] = [max(r[i] for r in rows) for i in range(len(rows[0]))]
        self.accessors.append(a)
        return len(self.accessors) - 1

    def save(self, path, doc):
        doc["asset"] = {"version": "2.0", "generator": "kiln mods/samples/PlayerStates/make_assets.py"}
        doc["bufferViews"], doc["accessors"] = self.views, self.accessors
        doc["buffers"] = [{"byteLength": len(self.blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(self.blob)).decode()}]
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        Path(path).write_text(json.dumps(doc, indent=None, separators=(",", ":")) + "\n")


def riposte(path):
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
    for name, motion in MOTIONS.items():
        length = motion[0]
        n = round(length * FPS)
        times = [i / FPS for i in range(n + 1)]
        tin = w.add("<f", [(round(t, 6),) for t in times], "SCALAR", minmax=True)
        rows = {"arm_r": [], "forearm_r": [], "hand_r": []}
        for t in times:
            for j, q in zip(rows, locals_of(pose_at(motion, t))):
                rows[j].append(q)
        samplers, channels = [], []
        for j, qs in rows.items():
            samplers.append({"input": tin, "output": w.add("<4f", qs, "VEC4"), "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": index[j], "path": "rotation"}})
        anims.append({"name": name, "samplers": samplers, "channels": channels})
    w.save(path, {"scene": 0, "scenes": [{"nodes": [0]}], "nodes": nodes, "animations": anims})


if __name__ == "__main__":
    out = HERE / "assets" / "src" / "riposte.gltf"
    riposte(out)
    print(f"make_assets: {out}: {', '.join(MOTIONS)}")
