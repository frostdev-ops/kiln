# SPDX-License-Identifier: MIT
"""Author a small skinned creature in Blender and export it with Blender's own glTF exporter, the path a modder takes, then write where Blender's own armature modifier puts every
vertex at some frames of each animation. Run by run_blender_check.py.

    blender -b --factory-startup --python tools/modelcook/tests/blender_skin_author.py -- OUT.glb OUT.eval.json
"""
import json
import math
import sys

import bpy

out, eval_out = sys.argv[sys.argv.index("--") + 1:][:2]
scene = bpy.context.scene
scene.render.fps = 60
for o in list(bpy.data.objects):  # the startup scene's cube
    bpy.data.objects.remove(o)

# An armature object that is moved, turned and scaled, with a mesh object that has a transform of its own: the file's inverse bind matrices have to carry all of that.
bpy.ops.object.armature_add(enter_editmode=True, location=(0.5, -0.2, 0.25))
arm = bpy.context.object
arm.name = "Creature"
arm.rotation_euler = (0, 0, math.radians(20))
arm.scale = (1.25, 1.25, 1.25)
eb = arm.data.edit_bones
eb.remove(eb[0])
SPEC = [("hips", None, (0, 0, 1.0), (0, 0, 1.3)), ("spine", "hips", (0, 0, 1.3), (0, 0, 1.7)), ("head", "spine", (0, 0, 1.7), (0, 0.1, 2.0)),
        ("arm_l", "spine", (0.1, 0, 1.6), (0.6, 0, 1.6)), ("hand_l", "arm_l", (0.6, 0, 1.6), (0.9, 0.1, 1.6)),
        ("arm_r", "spine", (-0.1, 0, 1.6), (-0.6, 0, 1.6)), ("hand_r", "arm_r", (-0.6, 0, 1.6), (-0.9, 0.1, 1.6)),
        ("leg_l", "hips", (0.15, 0, 1.0), (0.15, 0, 0.0)), ("leg_r", "hips", (-0.15, 0, 1.0), (-0.15, 0, 0.0))]
for name, parent, head, tail in SPEC:
    b = eb.new(name)
    b.head, b.tail = head, tail
    if parent:
        b.parent = eb[parent]
bpy.ops.object.mode_set(mode="OBJECT")


def dist_to_bone(p, head, tail):
    d = [tail[i] - head[i] for i in range(3)]
    t = sum((p[i] - head[i]) * d[i] for i in range(3)) / (sum(x * x for x in d) or 1)
    t = max(0.0, min(1.0, t))
    return math.dist(p, [head[i] + d[i] * t for i in range(3)])


verts, faces = [], []
SIDES, RINGS = 8, 6
for _, _, head, tail in SPEC:
    d = [tail[i] - head[i] for i in range(3)]
    ln = math.sqrt(sum(x * x for x in d))
    u = [x / ln for x in d]
    a = (1, 0, 0) if abs(u[0]) < 0.9 else (0, 1, 0)
    s = [a[1] * u[2] - a[2] * u[1], a[2] * u[0] - a[0] * u[2], a[0] * u[1] - a[1] * u[0]]
    sl = math.sqrt(sum(x * x for x in s))
    s = [x / sl for x in s]
    f = [u[1] * s[2] - u[2] * s[1], u[2] * s[0] - u[0] * s[2], u[0] * s[1] - u[1] * s[0]]
    first = len(verts)
    for r in range(RINGS + 1):
        for k in range(SIDES):
            th = 2 * math.pi * k / SIDES
            verts.append(tuple(head[i] + d[i] * r / RINGS + 0.06 * (math.cos(th) * s[i] + math.sin(th) * f[i]) for i in range(3)))
    for r in range(RINGS):
        for k in range(SIDES):
            n = (k + 1) % SIDES
            faces.append((first + r * SIDES + k, first + r * SIDES + n, first + (r + 1) * SIDES + n, first + (r + 1) * SIDES + k))
me = bpy.data.meshes.new("Body")
me.from_pydata(verts, [], faces)
ob = bpy.data.objects.new("Body", me)
scene.collection.objects.link(ob)
for name, _, _, _ in SPEC:
    ob.vertex_groups.new(name=name)
for vi, p in enumerate(verts):  # smooth weights: what auto weights give, with no dependence on the solver
    ws = sorted(((1 / (dist_to_bone(p, h, t) + 0.05) ** 4, nm) for nm, _, h, t in SPEC), reverse=True)[:4]
    tot = sum(w for w, _ in ws)
    for w, nm in ws:
        ob.vertex_groups[nm].add([vi], w / tot, "REPLACE")
ob.parent = arm
ob.parent_type = "OBJECT"
ob.location = (0.3, 0.1, 0.0)
ob.modifiers.new("Armature", "ARMATURE").object = arm


def action(name, frames, keys):
    bpy.context.view_layer.objects.active = arm
    ad = arm.animation_data or arm.animation_data_create()
    ad.action = bpy.data.actions.new(name)
    ad.action.use_fake_user = True
    for bone, ks in keys.items():
        pb = arm.pose.bones[bone]
        pb.rotation_mode = "XYZ"
        for f, e in ks:
            pb.rotation_euler = [math.radians(x) for x in e]
            pb.keyframe_insert("rotation_euler", frame=f)
    arm.data.pose_position = "POSE"
    track = ad.nla_tracks.new()
    track.name = name
    track.strips.new(name, frames[0], ad.action)
    ad.action = None
    for pb in arm.pose.bones:
        pb.rotation_euler = (0, 0, 0)


ACTIONS = {"Bend": (1, 61, {"spine": [(1, (0, 0, 0)), (61, (40, 0, 20))], "arm_r": [(1, (0, 0, 0)), (61, (0, 0, -70))], "hand_r": [(1, (0, 0, 0)), (61, (30, 40, 0))],
                            "head": [(1, (0, 0, 0)), (61, (-30, 20, 0))], "leg_l": [(1, (0, 0, 0)), (61, (60, 0, 0))]}),
           "Wave": (1, 41, {"arm_l": [(1, (0, 0, 0)), (21, (0, 0, 80)), (41, (0, 0, 0))], "hips": [(1, (0, 0, 0)), (21, (0, 30, 0)), (41, (0, 0, 0))]})}
for name, (a, b, keys) in ACTIONS.items():
    action(name, (a, b), keys)

bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_animations=True, export_animation_mode="NLA_TRACKS", export_skins=True, export_yup=True)

ad = arm.animation_data
ad.use_nla = False
result = {}
for name, (a, b, _) in ACTIONS.items():
    act = bpy.data.actions[name]
    ad.action = act
    ad.action_slot = next((s for s in act.slots if s.target_id_type == "OBJECT"), act.slots[0])
    for frame in (a, (a + b) // 2, b):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        dg = bpy.context.evaluated_depsgraph_get()
        ev = ob.evaluated_get(dg)
        m = ev.to_mesh()
        world = ev.matrix_world
        # Blender's z-up world -> the file's y-up frame: (x, z, -y)
        pts = [(q.x, q.z, -q.y) for q in (world @ v.co for v in m.vertices)]
        ev.to_mesh_clear()
        result[f"{name}:{frame - a}"] = pts
json.dump({"vertices": len(verts), "poses": result}, open(eval_out, "w"))
print("BLENDER exported", out, len(verts), "vertices", flush=True)
