# SPDX-License-Identifier: MIT
"""What a modder does with `kiln skeleton player`, in Blender: import it, model around the body, weight to its bones, add a shape key named Female, export the whole scene with Blender's own glTF
exporter. Run by game_worn_check.py blender, which compares what comes out of the cook with where Blender has the vertices.

    blender -b --factory-startup --python tools/modelcook/tests/blender_worn_author.py -- REFERENCE.glb OUT.glb OUT.eval.json
"""
import json
import sys

import bmesh
import bpy
from mathutils import Vector

reference, out, eval_out = sys.argv[sys.argv.index("--") + 1:][:3]
for o in list(bpy.data.objects):  # the startup scene's cube
    bpy.data.objects.remove(o)
bpy.ops.import_scene.gltf(filepath=reference)
arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
head = next(b for b in arm.data.bones if b.name.endswith("_Head"))
neck = head.parent
at = arm.matrix_world @ head.head_local
print("BLENDER head bone", head.name, "at", tuple(round(c, 4) for c in at), "neck", neck.name, "objects", [o.name for o in bpy.data.objects])

me = bpy.data.meshes.new("helmet")
bm = bmesh.new()
bmesh.ops.create_uvsphere(bm, u_segments=16, v_segments=10, radius=0.16)
bm.to_mesh(me)
bm.free()
ob = bpy.data.objects.new("helmet", me)
bpy.context.collection.objects.link(ob)
ob.location = at + Vector((0, 0, 0.08))
mat = bpy.data.materials.new("steel")
me.materials.append(mat)
mod = ob.modifiers.new("Armature", "ARMATURE")
mod.object = arm
ob.parent = arm
ob.matrix_parent_inverse = arm.matrix_world.inverted()
ge = ob.vertex_groups.new(name=head.name)
gn = ob.vertex_groups.new(name=neck.name)
for v in me.vertices:
    upper = max(0.0, min(1.0, (v.co.z + 0.16) / 0.1))  # the lowest vertices share with the neck
    ge.add([v.index], upper, "REPLACE")
    if upper < 1:
        gn.add([v.index], 1 - upper, "REPLACE")
ob.shape_key_add(name="Basis")
key = ob.shape_key_add(name="Female")
for i, v in enumerate(me.vertices):
    key.data[i].co = v.co * Vector((0.9, 0.9, 0.97))
other = ob.shape_key_add(name="Dented")  # a shape key the game has no use for
for i, v in enumerate(me.vertices):
    other.data[i].co = v.co * 1.2

bpy.context.view_layer.update()
world = ob.matrix_world


def gl(p):
    return (p.x, p.z, -p.y)


positions = [gl(world @ v.co) for v in me.vertices]
deltas = [gl(world.to_3x3() @ (key.data[i].co - v.co)) for i, v in enumerate(me.vertices)]
bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_skins=True, export_morph=True, export_animations=False)
with open(eval_out, "w") as f:
    json.dump({"positions": positions, "female": deltas, "head": head.name, "neck": neck.name}, f)
print("BLENDER exported", out, len(positions), "vertices")
