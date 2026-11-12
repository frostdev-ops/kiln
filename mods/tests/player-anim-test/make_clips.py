#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes the authored clips of the player-anim lab mods as glTF, into a mod folder that is a copy outside the tree (run.py makes it in
the lab): every motion is ours, a few joints turned by angles written here; the file also holds the player's skeleton and rest pose,
read from the installed game through `modelcook.py skeleton`, so it is game-derived and is never written into the repository.

    python3 mods/tests/player-anim-test/make_clips.py MOD_DIR [--game DIR]

MOD_DIR is a copy of one of mods/<name>/ (its kiln.mod.json says which clips it needs: the "clips" table below, by mod id). Writes
MOD_DIR/assets/src/<mod>.gltf with one animation per clip, named as the clip; a mod of the "rig" kind gets a skeleton of its own instead
(no game data at all). Each clip is 2 s at 30 keys a second, LINEAR.

What each moves, in the joint's own frame, as an extra turn on its rest rotation (joints by index on the player's skeleton: j70 is the
right upper arm, j72 the right forearm, j40 the left upper arm, j42 the left forearm; the chain from the HandR slot's joint up is
94 74 73 72 71 70 69 15): the rest of the body holds its rest pose, a pose no game clip holds, so where the hands are tells the clip
apart from any of the game's.
"""
import argparse
import base64
import json
import math
import struct
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
SECONDS, RATE = 2.0, 30

# mod id -> clip name -> [(joint, axis, peak degrees, shape)]; shape "arc": 0 -> peak -> 0, "hold": peak with a small sway, "ramp": 0 -> peak
CLIPS = {
    "pa-a": {
        "a_slash": [(70, "z", 110, "arc"), (72, "z", -70, "arc")],
        "a_guard": [(70, "z", 80, "hold"), (72, "z", -90, "hold")],
        "a_leap": [(70, "x", 90, "arc"), (40, "x", 90, "arc")],
        "a_bolt": [(40, "z", -110, "arc"), (42, "z", 70, "arc")],
    },
    "pa-b": {
        "b_slash": [(40, "z", -100, "arc"), (42, "z", 60, "arc")],
        "b_smash": [(70, "y", 120, "arc"), (72, "x", -60, "arc")],
    },
}
AXES = {"x": (1, 0, 0), "y": (0, 1, 0), "z": (0, 0, 1)}


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return [aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx, aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz]


def turn(axis, deg):
    h = math.radians(deg) / 2
    x, y, z = AXES[axis]
    return [x * math.sin(h), y * math.sin(h), z * math.sin(h), math.cos(h)]


def amount(shape, t):
    u = t / SECONDS
    if shape == "arc":
        return math.sin(math.pi * u)
    if shape == "hold":
        return 1.0 + 0.12 * math.sin(2 * math.pi * u)
    return u


class Buf:
    def __init__(self, doc):
        self.doc, self.blob = doc, bytearray()

    def add(self, data, comp, typ, count, mn=None, mx=None):
        while len(self.blob) % 4:
            self.blob.append(0)
        self.doc["bufferViews"].append({"buffer": len(self.doc["buffers"]), "byteOffset": len(self.blob), "byteLength": len(data)})
        self.blob += data
        a = {"bufferView": len(self.doc["bufferViews"]) - 1, "componentType": comp, "type": typ, "count": count}
        if mn is not None:
            a["min"], a["max"] = mn, mx
        self.doc["accessors"].append(a)
        return len(self.doc["accessors"]) - 1

    def close(self):
        self.doc["buffers"].append({"byteLength": len(self.blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(self.blob)).decode()})


def player_clips(mod_dir, mod, game):
    src = mod_dir / "assets/src"
    src.mkdir(parents=True, exist_ok=True)
    skel = src / "player.gltf"
    subprocess.run([sys.executable, str(REPO / "tools/modelcook/modelcook.py"), "--game", str(game), "skeleton", str(skel), "--body", "none"],
                   check=True, stdout=subprocess.DEVNULL)
    doc = json.loads(skel.read_text())
    node_of = {n.get("extras", {}).get("keen_joint"): i for i, n in enumerate(doc["nodes"]) if "keen_joint" in n.get("extras", {})}
    b = Buf(doc)
    times = [i / RATE for i in range(int(SECONDS * RATE) + 1)]
    t_acc = b.add(struct.pack(f"<{len(times)}f", *times), 5126, "SCALAR", len(times), [0.0], [SECONDS])
    doc["animations"] = []
    for clip, moves in CLIPS[mod].items():
        samplers, channels = [], []
        for joint, axis, peak, shape in moves:
            node = doc["nodes"][node_of[joint]]
            rest = node.get("rotation", [0, 0, 0, 1])
            keys = [qmul(rest, turn(axis, peak * amount(shape, t))) for t in times]
            q_acc = b.add(b"".join(struct.pack("<4f", *q) for q in keys), 5126, "VEC4", len(keys))
            samplers.append({"input": t_acc, "output": q_acc, "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": node_of[joint], "path": "rotation"}})
        doc["animations"].append({"name": clip, "samplers": samplers, "channels": channels})
    b.close()
    out = src / f"{mod}.gltf"
    out.write_text(json.dumps(doc))
    skel.unlink()
    return out


def rig_clip(mod_dir, mod):
    """A skeleton of our own (two joints, a stick 1 m long), with one clip: a clip made for another skeleton than the player's."""
    src = mod_dir / "assets/src"
    src.mkdir(parents=True, exist_ok=True)
    doc = {"asset": {"version": "2.0", "generator": "player-anim-test make_clips.py"}, "scene": 0, "scenes": [{"nodes": [0, 2]}],
           "nodes": [{"name": "base", "children": [1]}, {"name": "tip", "translation": [0, 1, 0]}, {"name": "stick", "mesh": 0, "skin": 0}],
           "materials": [{"name": "stick"}], "buffers": [], "bufferViews": [], "accessors": []}
    b = Buf(doc)
    pos = [(-0.02, 0, 0), (0.02, 0, 0), (0, 1, 0)]
    p = b.add(b"".join(struct.pack("<3f", *v) for v in pos), 5126, "VEC3", 3, [-0.02, 0, 0], [0.02, 1, 0])
    j = b.add(b"".join(struct.pack("<4B", *v) for v in ((0, 0, 0, 0), (0, 0, 0, 0), (1, 0, 0, 0))), 5121, "VEC4", 3)
    w = b.add(b"".join(struct.pack("<4f", 1, 0, 0, 0) for _ in pos), 5126, "VEC4", 3)
    i = b.add(struct.pack("<3H", 0, 1, 2), 5123, "SCALAR", 3)
    ibm = b.add(struct.pack("<32f", *([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1] + [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1, 0, 1])), 5126, "MAT4", 2)
    doc["meshes"] = [{"name": "stick", "primitives": [{"attributes": {"POSITION": p, "JOINTS_0": j, "WEIGHTS_0": w}, "indices": i, "material": 0}]}]
    doc["skins"] = [{"joints": [0, 1], "skeleton": 0, "inverseBindMatrices": ibm}]
    times = [k / RATE for k in range(int(SECONDS * RATE) + 1)]
    t_acc = b.add(struct.pack(f"<{len(times)}f", *times), 5126, "SCALAR", len(times), [0.0], [SECONDS])
    q_acc = b.add(b"".join(struct.pack("<4f", *turn("z", 45 * amount("arc", t))) for t in times), 5126, "VEC4", len(times))
    doc["animations"] = [{"name": "x_wave", "samplers": [{"input": t_acc, "output": q_acc, "interpolation": "LINEAR"}],
                          "channels": [{"sampler": 0, "target": {"node": 1, "path": "rotation"}}]}]
    b.close()
    out = src / f"{mod}.gltf"
    out.write_text(json.dumps(doc))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("mod_dir")
    ap.add_argument("--game", default=str(Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"))
    ap.add_argument("--rig", action="store_true", help="the mod's clip is on a skeleton of its own (pa-bad-skeleton)")
    a = ap.parse_args()
    mod_dir = Path(a.mod_dir).resolve()
    if REPO in mod_dir.parents or mod_dir == REPO:
        sys.exit("make_clips.py: MOD_DIR is inside the repository; the player's clips hold the game's skeleton: write them into a copy (run.py does)")
    mod = json.loads((mod_dir / "kiln.mod.json").read_text())["id"]
    out = rig_clip(mod_dir, mod) if a.rig else player_clips(mod_dir, mod, Path(a.game).expanduser())
    print(f"make_clips: {out}")


if __name__ == "__main__":
    main()
