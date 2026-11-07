#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Builds the test's assets on your machine, with no game data: the rigged sample figure as the body (models/figure.glb), the same figure's animations on a
rig of other joint names and 1.3 times the size (models/pack.gltf), the cook of the mod's blocks, and what the lab test expects (assets/cooked/expect.json):
the motion as authored on the body's own rig, cooked without retargeting.

    python3 mods/tests/retarget-test/build-assets.py
"""
import base64
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent.parent
sys.path[:0] = [str(ROOT / "tools/kfc"), str(ROOT / "tools/animcook"), str(ROOT / "tools/modelcook")]
SCALE = 1.3

(HERE / "models").mkdir(exist_ok=True)
subprocess.check_call([sys.executable, str(ROOT / "tools/modelcook/samples/rigged_figure.py"), str(HERE / "models/figure.glb")])

from anicook import clips as C, retarget as RT, skeleton as K  # noqa: E402
from anicook.gltfdoc import Doc  # noqa: E402
from cook.gltf import _load  # noqa: E402
from kfc import anim  # noqa: E402

doc, bins = _load(HERE / "models/figure.glb")
joints = set(doc["skins"][0]["joints"])
for i in joints:
    n = doc["nodes"][i]
    n["name"] = "pack:" + n["name"]
    if "translation" in n:
        n["translation"] = [c * SCALE for c in n["translation"]]
blob = bytearray(bins[0])
# translation channels (root motion and such) travel 1.3 times as far on the bigger rig
import struct  # noqa: E402
for a in doc.get("animations", []):
    for ch in a["channels"]:
        if ch["target"]["path"] == "translation" and ch["target"]["node"] in joints:
            acc = doc["accessors"][a["samplers"][ch["sampler"]]["output"]]
            v = doc["bufferViews"][acc["bufferView"]]
            o = v.get("byteOffset", 0) + acc.get("byteOffset", 0)
            vals = struct.unpack_from(f"<{acc['count'] * 3}f", blob, o)
            struct.pack_into(f"<{len(vals)}f", blob, o, *[x * SCALE for x in vals])
doc["buffers"] = [{"byteLength": len(blob), "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()}]
(HERE / "models/pack.gltf").write_text(json.dumps(doc))

subprocess.check_call([sys.executable, str(ROOT / "tools/animcook/animcook.py"), "--no-game", "recipe", str(HERE / "assets/cook.json"), "--update-manifest"])

body = Doc(HERE / "models/figure.glb")
sk = K.from_gltf(body)
hand = sk.index["hand_r"]


def hand_heights(clip_bytes):
    c = anim.parse(clip_bytes)
    dp = sk.default_pose()
    return [RT.model_space(sk, anim.sample_frame(c, f, dp))[hand][0][1] for f in range(c.frame_count)]


direct, _, _, _ = C.cook_clip(body, "Attack", sk)
truth = hand_heights(direct)
cooked = hand_heights((HERE / "assets/cooked/clip_figure.Attack.once.bin").read_bytes())
graph = json.loads((HERE / "assets/cooked/pack-moves.figure-body.graph.json").read_text())
expect = {"slamState": graph["states"]["slam"]["id"], "idleState": graph["states"]["Idle"]["id"],
          "handHighest": max(truth), "handRest": RT.model_space(sk, RT.rest_frame(sk))[hand][0][1],
          "offlineWorst": max(abs(a - b) for a, b in zip(truth, cooked))}
(HERE / "assets/cooked/expect.json").write_text(json.dumps(expect, indent=1))
print("expect:", expect)
