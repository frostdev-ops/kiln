# SPDX-License-Identifier: MIT
"""Author a small skinned creature in Blender itself and export it with Blender's own glTF exporter: the path a modder takes. Run by run_blender_check.py.

    blender -b --factory-startup --python tools/animcook/tests/blender_author.py -- OUT.glb
"""
import math
import sys

import bpy

out = sys.argv[sys.argv.index("--") + 1]
scene = bpy.context.scene
scene.render.fps = 60

bpy.ops.object.armature_add(enter_editmode=True, location=(0.5, 0, 0.25))  # not at the origin, and turned: an armature object above the root
arm = bpy.context.object
arm.name = "Creature"
arm.rotation_euler = (0, 0, math.radians(20))
eb = arm.data.edit_bones
eb.remove(eb[0])
spec = [("hips", None, (0, 0, 1.0), (0, 0, 1.3)), ("spine", "hips", (0, 0, 1.3), (0, 0, 1.7)), ("head", "spine", (0, 0, 1.7), (0, 0.1, 2.0)),
        ("arm_l", "spine", (0.1, 0, 1.6), (0.6, 0, 1.6)), ("hand_l", "arm_l", (0.6, 0, 1.6), (0.9, 0.1, 1.6)),
        ("arm_r", "spine", (-0.1, 0, 1.6), (-0.6, 0, 1.6)), ("hand_r", "arm_r", (-0.6, 0, 1.6), (-0.9, 0.1, 1.6)),
        ("leg_l", "hips", (0.15, 0, 1.0), (0.15, 0, 0.0)), ("leg_r", "hips", (-0.15, 0, 1.0), (-0.15, 0, 0.0))]
for name, parent, head, tail in spec:
    b = eb.new(name)
    b.head, b.tail = head, tail
    if parent:
        b.parent = eb[parent]
bpy.ops.object.mode_set(mode="OBJECT")

# a mesh with automatic-free weights: one cube per bone, weighted to it
verts, faces, groups = [], [], []
for ix, (name, _, head, tail) in enumerate(spec):
    n = len(verts)
    for dx, dy, t in [(-.05, -.05, 0), (.05, -.05, 0), (.05, .05, 0), (-.05, .05, 0), (-.05, -.05, 1), (.05, -.05, 1), (.05, .05, 1), (-.05, .05, 1)]:
        verts.append(tuple(head[i] + (tail[i] - head[i]) * t for i in range(3)))
        verts[-1] = (verts[-1][0] + dx, verts[-1][1] + dy, verts[-1][2])
    for f in [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]:
        faces.append(tuple(n + i for i in f))
    groups.append((name, list(range(n, n + 8))))
me = bpy.data.meshes.new("Body")
me.from_pydata(verts, [], faces)
ob = bpy.data.objects.new("Body", me)
scene.collection.objects.link(ob)
for name, vs in groups:
    ob.vertex_groups.new(name=name).add(vs, 1.0, "REPLACE")
ob.parent = arm
ob.modifiers.new("Armature", "ARMATURE").object = arm


def action(name, frames, keys):
    """keys: {bone: [(frame, euler degrees or None, location or None, scale or None)]}"""
    bpy.context.view_layer.objects.active = arm
    ad = arm.animation_data or arm.animation_data_create()
    ad.action = bpy.data.actions.new(name)
    ad.action.use_fake_user = True
    for bone, ks in keys.items():
        pb = arm.pose.bones[bone]
        pb.rotation_mode = "XYZ"
        for f, e, loc, sc in ks:
            if e is not None:
                pb.rotation_euler = [math.radians(x) for x in e]
                pb.keyframe_insert("rotation_euler", frame=f)
            if loc is not None:
                pb.location = loc
                pb.keyframe_insert("location", frame=f)
            if sc is not None:
                pb.scale = sc
                pb.keyframe_insert("scale", frame=f)
    arm.data.pose_position = "POSE"
    tr = ad.action_tracks if hasattr(ad, "action_tracks") else None
    track = ad.nla_tracks.new()
    track.name = name
    track.strips.new(name, frames[0], ad.action)
    ad.action = None
    for pb in arm.pose.bones:
        pb.rotation_euler = (0, 0, 0)
        pb.location = (0, 0, 0)
        pb.scale = (1, 1, 1)


action("Idle", (1, 121), {"arm_l": [(1, (0, 0, -30), None, None), (61, (0, 0, 30), None, None), (121, (0, 0, -30), None, None)],
                          "head": [(1, (0, 0, 0), None, None), (61, (20, 10, 0), None, None), (121, (0, 0, 0), None, None)]})
action("Walk", (1, 61), {"leg_l": [(1, (40, 0, 0), None, None), (31, (-40, 0, 0), None, None), (61, (40, 0, 0), None, None)],
                         "leg_r": [(1, (-40, 0, 0), None, None), (31, (40, 0, 0), None, None), (61, (-40, 0, 0), None, None)],
                         "hips": [(1, None, (0, 0.05, 0), None), (16, None, (0, -0.05, 0), None), (31, None, (0, 0.05, 0), None), (46, None, (0, -0.05, 0), None), (61, None, (0, 0.05, 0), None)]})
action("Grow", (1, 41), {"hips": [(1, None, None, (1, 1, 1)), (41, None, None, (1.5, 1.5, 1.5))],
                         "hand_r": [(1, (0, 0, 0), None, (1, 1, 1)), (41, (0, 90, 0), None, (0.5, 0.5, 0.5))]})

bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_animations=True, export_animation_mode="NLA_TRACKS", export_skins=True, export_yup=True)
print("BLENDER exported", out, flush=True)
