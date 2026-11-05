# SPDX-License-Identifier: MIT
"""An independent reader for the cook: Blender imports the author's glTF and a glTF exported back from our cooked skeleton and clip (decoded by tools/kfc), and evaluates both
armatures itself; the joints' world positions must agree frame by frame. Run by run_blender_check.py, not by the test suite: it needs Blender.

    blender -b --factory-startup --python tools/animcook/tests/blender_check.py -- SOURCE.glb PAIRS.json
    PAIRS.json: [{"animation": "Idle", "file": "cooked_idle.gltf", "frames": 120}, ...]
"""
import json
import sys

import bpy

argv = sys.argv[sys.argv.index("--") + 1:]
source, pairs = argv[0], json.load(open(argv[1]))
scene = bpy.context.scene
scene.render.fps = 60


def load(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    arm = next(o for o in new if o.type == "ARMATURE")
    return arm


def set_action(arm, name):
    action = next(a for a in bpy.data.actions if a.name.startswith(name) and len(a.slots))
    ad = arm.animation_data or arm.animation_data_create()
    ad.action = action
    ad.action_slot = next((s for s in action.slots if s.target_id_type == "OBJECT"), action.slots[0])
    return action


def heads(arm, frame):
    scene.frame_set(int(frame), subframe=frame - int(frame))
    bpy.context.view_layer.update()
    return {b.name: arm.matrix_world @ b.head for b in arm.pose.bones}


src = load(source)
order = json.load(open(argv[1].replace(".json", ".joints.json")))
worst_all = 0.0
for p in pairs:
    names_before = set(bpy.data.actions)
    action = set_action(src, p["animation"])
    start = action.frame_range[0]
    cooked = load(p["file"])
    new_actions = [a for a in bpy.data.actions if a not in names_before]
    ca = new_actions[0]
    cad = cooked.animation_data or cooked.animation_data_create()
    cad.action = ca
    cad.action_slot = next((s for s in ca.slots if s.target_id_type == "OBJECT"), ca.slots[0])
    cstart = ca.frame_range[0]
    worst = 0.0
    n = p["frames"]
    for f in range(0, n + 1, max(1, n // 40)):
        a = heads(src, start + f)
        b = heads(cooked, cstart + f)
        for i, name in enumerate(order):
            if name not in a or f"j{i}" not in b:
                continue
            worst = max(worst, (a[name] - b[f"j{i}"]).length)
    worst_all = max(worst_all, worst)
    print(f"BLENDER {p['animation']}: {len(order)} joints, {n} frames, largest joint distance {worst * 1000:.3f} mm", flush=True)
# a control: this test can fail. Two different animations of the source must differ.
acts = [p["animation"] for p in pairs][:2]
set_action(src, acts[0])
start = next(x for x in bpy.data.actions if x.name.startswith(acts[0])).frame_range[0]
a = heads(src, start + 15)
set_action(src, acts[1])
start = next(x for x in bpy.data.actions if x.name.startswith(acts[1])).frame_range[0]
b = heads(src, start + 15)
print(f"BLENDER control, {acts[0]} against {acts[1]}: largest joint distance {max((a[n] - b[n]).length for n in a) * 1000:.1f} mm", flush=True)
print(f"BLENDER RESULT largest {worst_all * 1000:.3f} mm", flush=True)
