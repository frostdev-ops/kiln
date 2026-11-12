#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The sample's three animations, written as glTF on the player's skeleton: assets/local/riposte.gltf.

    python3 mods/samples/PlayerStates/make_clips.py [--game DIR]

The motions are ours: a few joints of the arms turned by the angles below, on top of the skeleton's rest pose; the rest of the body
holds that rest pose. The skeleton and its rest pose come from your installed game (`modelcook.py skeleton`), so the file is made on
your machine and is never committed or shipped (.gitignore: assets/local/). Then cook: `python3 tools/animcook/animcook.py recipe
mods/samples/PlayerStates/assets/cook.json --update-manifest`.

A real mod authors its clips in Blender on the same skeleton (`kiln skeleton player player.gltf` writes it, with a body to animate
around) and exports glTF; this script only shows the shape of the data. The joints, by index on the player's skeleton: j70 the right
upper arm, j72 the right forearm, j40 the left upper arm, j42 the left forearm (the chain from the right hand's slot up is
94 74 73 72 71 70 69 15). Angles are an extra turn in the joint's own frame, in degrees.
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
RATE = 30

# name: (seconds, loop, [(joint, axis, [(time, degrees), ...])])
CLIPS = {
    # a swing from the right hip up over the left shoulder: the upper arm comes forward and across, the forearm folds
    "swing": (1.2, False, [(70, "y", [(0, 0), (0.35, 40), (0.7, 85), (1.2, 0)]), (70, "z", [(0, 0), (0.35, 60), (0.7, 110), (1.2, 0)]),
                           (72, "z", [(0, 0), (0.35, -30), (0.7, -80), (1.2, 0)])]),
    # an overhead chop: the arm goes up behind the head and comes down in front
    "heavy": (1.8, False, [(70, "x", [(0, 0), (0.6, -150), (0.9, -150), (1.2, 20), (1.8, 0)]), (72, "x", [(0, 0), (0.6, -60), (1.2, 0), (1.8, 0)])]),
    # the guard: both arms up, forearms bent, the sword held flat above the head; a slow sway so the loop is seen to run
    "guard": (2.0, True, [(70, "z", [(0, 120), (1.0, 112), (2.0, 120)]), (72, "z", [(0, -90), (2.0, -90)]),
                          (40, "z", [(0, -120), (1.0, -112), (2.0, -120)]), (42, "z", [(0, 90), (2.0, 90)])]),
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


def angle(keys, t):
    """Degrees at time t, eased between the keys."""
    for (t0, a0), (t1, a1) in zip(keys, keys[1:]):
        if t0 <= t <= t1:
            u = (t - t0) / (t1 - t0) if t1 > t0 else 1.0
            return a0 + (a1 - a0) * (0.5 - 0.5 * math.cos(math.pi * u))
    return keys[-1][1]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=str(Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"))
    ap.add_argument("--assets", default=str(HERE / "assets"), help="the mod's assets folder (default: this sample's); the file goes into its local/")
    a = ap.parse_args()
    local = Path(a.assets) / "local"
    local.mkdir(parents=True, exist_ok=True)
    skel = local / "player.gltf"
    subprocess.run([sys.executable, str(REPO / "tools/modelcook/modelcook.py"), "--game", a.game, "skeleton", str(skel), "--body", "none"],
                   check=True, stdout=subprocess.DEVNULL)
    doc = json.loads(skel.read_text())
    node_of = {n.get("extras", {}).get("keen_joint"): i for i, n in enumerate(doc["nodes"]) if "keen_joint" in n.get("extras", {})}
    blob = bytearray()

    def add(data, comp, typ, count, mn=None, mx=None):
        while len(blob) % 4:
            blob.append(0)
        doc["bufferViews"].append({"buffer": len(doc["buffers"]), "byteOffset": len(blob), "byteLength": len(data)})
        blob.extend(data)
        acc = {"bufferView": len(doc["bufferViews"]) - 1, "componentType": comp, "type": typ, "count": count}
        if mn is not None:
            acc["min"], acc["max"] = mn, mx
        doc["accessors"].append(acc)
        return len(doc["accessors"]) - 1

    doc["animations"] = []
    for name, (seconds, loop, moves) in CLIPS.items():
        times = [i / RATE for i in range(int(seconds * RATE) + 1)]
        t_acc = add(struct.pack(f"<{len(times)}f", *times), 5126, "SCALAR", len(times), [0.0], [times[-1]])
        per_joint = {}
        for joint, axis, keys in moves:
            per_joint.setdefault(joint, []).append((axis, keys))
        samplers, channels = [], []
        for joint, turns in per_joint.items():
            rest = doc["nodes"][node_of[joint]].get("rotation", [0, 0, 0, 1])
            out = []
            for t in times:
                q = rest
                for axis, keys in turns:
                    q = qmul(q, turn(axis, angle(keys, t)))
                out.append(q)
            q_acc = add(b"".join(struct.pack("<4f", *q) for q in out), 5126, "VEC4", len(out))
            samplers.append({"input": t_acc, "output": q_acc, "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": node_of[joint], "path": "rotation"}})
        doc["animations"].append({"name": name, "samplers": samplers, "channels": channels})
    doc["buffers"].append({"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()})
    (local / "riposte.gltf").write_text(json.dumps(doc))
    skel.unlink()
    print(f"make_clips: {local / 'riposte.gltf'}: {', '.join(CLIPS)}")


if __name__ == "__main__":
    main()
