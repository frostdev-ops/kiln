# SPDX-License-Identifier: MIT
"""The cook steps behind the CLI and cook.json recipes. Each step reads vanilla inputs from the installed game (read
only) and returns the bytes it produced plus the kiln.assets entries that serve them (file names relative to the
mod's assets/ folder)."""
import hashlib
import json
import re
import struct
from pathlib import Path

from kfc.archive import ResourceKey
from kfc.blob import Decoder
from kfc.blobwrite import Encoder
from kfc.util import guid_bytes, guid_text

from . import gltf, material as MB, rendermodel as RM, skin as SK
from .validate import validate


class StepError(Exception):
    pass


# The one skeleton of the player, for both bodies, and the animation graph that says which of its joints a slot is.
PLAYER_SKELETON = "87b29df6-6518-43ef-9298-e3f85a6204b9"
GRAPH_TYPE = "keen::anim_graph::runtime_graph::AnimationGraphResource2_0"


def _norm_guid(g):
    return guid_text(guid_bytes(g))


def _material_entry(k, t, guid, base_materials, own=None):
    """A model's RenderModelMaterial: a MaterialBlock of this mod by name (own: {"blocks", "mod"}), or a game material by GUID."""
    if not MB.GUID.match(str(guid)):
        got = MB.entry(guid, own["blocks"], own["mod"]) if own else None
        if got is None:
            raise StepError(f"material {guid!r} is neither a GUID nor a block in this mod's blocks/*.json \"materials\"")
        return got
    guid = _norm_guid(guid)
    for m in base_materials:
        if m["material"] == guid:
            return dict(m)
    th = t.get("keen::RenderMaterialResource")["hash"]
    key = ResourceKey(guid, th, 0)
    if not k.has(key):
        raise StepError(f"material {guid} is not a keen::RenderMaterialResource in {k.path}")
    v = Decoder(t).decode(th, k.read(key))
    return {"material": guid, "renderEffectId": v["effectId"], "materialFlags": v["flags"]}


def game_target(k, t, guid):
    """A skeleton of the installed game, for binding a mesh to it."""
    ht = t.get("keen::ModelHierarchyResource")
    key = ResourceKey(guid, ht["hash"], 0)
    if not k.has(key):
        raise StepError(f"skeleton {guid} is not a keen::ModelHierarchyResource of this game")
    return SK.Target.from_game(Decoder(t, max_array=1 << 26).decode(ht, k.read(key)), key.guid)


def _graph_slots(value):
    """(skeleton, slotBoneIndexMapping) of a decoded AnimationGraphResource2_0."""
    for v in (value, *[x for x in value.values() if isinstance(x, dict)]):
        if "hierarchy" in v:
            return v["hierarchy"], v.get("slotBoneIndexMapping")
    return None, None


def slot_joints(k, t, skeleton):
    """{keen::AttachmentSlot name: joint} of a skeleton, from the first of the game's animation graphs on it. Slots no graph sets are left out, as is Root."""
    gt = t.get(GRAPH_TYPE)
    if gt is None:
        return {}
    names = [v["name"] for v in t.get("keen::AttachmentSlot")["values"]]
    dec = Decoder(t, max_array=1 << 26)
    for key in k.resources_of_type(gt["hash"]):
        hierarchy, table = _graph_slots(dec.decode(gt["hash"], k.read(key)))
        if hierarchy == skeleton and table:
            return {names[i]: j for i, j in enumerate(table) if j and i < len(names) and names[i] != "Count"}
    return {}


def attach_joint(k, t, target, name):
    """What "attach" says: a slot's name, as the game calls them (any case), or j<index>."""
    if isinstance(name, str) and name[:1] in "jJ" and name[1:].isdigit():
        return int(name[1:])
    slots = slot_joints(k, t, target.guid)
    for slot, joint in slots.items():
        if slot.lower() == str(name).lower():
            return joint
    raise StepError(f"attach: {name!r} is not a slot of skeleton {target.guid}; there are {', '.join(sorted(slots)) or 'none'}, or say j<index>")


def player_reference(k, t, out, body="male"):
    """The player's skeleton as a glTF to weight against, from the installed game, with a body in it to model around unless body is None. Joints are named j<index>, and j<index>_<slot> where a
    slot sits. The body is a mesh node named reference_body, which a cook leaves out, so an author can export the whole scene. Returns what it wrote: {"joints", "named", "body_parts", "vertices"}."""
    from kfc import mesh as M
    h = M.load_hierarchy(k, t, PLAYER_SKELETON)
    names = {i: f"j{i}" for i in range(h["jointCount"])}
    named = {}
    for slot, joint in slot_joints(k, t, PLAYER_SKELETON).items():
        if joint not in named and not slot.startswith("Generic"):
            named[joint] = slot
            names[joint] = f"j{joint}_{slot}"
    tri = M.Tris()
    parts = 0
    if body:
        th = t.get("keen::ItemInfo")["hash"]
        mh = t.get("keen::RenderModel")["hash"]
        dec = Decoder(t)
        female = body == "female"
        want = re.compile(rf"^VC_Body_(Head_{'Female' if female else 'Male'}_01_.*|(?!Head)\w+_01)$")
        for key in k.resources_of_type(th):
            v = dec.decode(th, k.read(key))
            if not want.match(v.get("debugName") or ""):
                continue
            eq = v["equipment"]
            model = eq.get("visualModel")
            if female:
                model = next((m["model"] for m in eq.get("visualModels") or [] if "Female" in m["mask"]), model)
            if model and k.has(ResourceKey(model, mh, 0)):
                tri.submeshes += M.to_rh(M.triangles(M.load_render_model(k, t, model), 0)).submeshes
                parts += 1
        if not parts:
            raise StepError("no body parts found in this game (VC_Body_*): say --body none")
    M.write_gltf(tri, out, name="reference_body", skeleton=M.skeleton_rh(h), joint_names=names)
    return {"joints": h["jointCount"], "named": {names[j]: s for j, s in named.items()}, "body_parts": parts, "vertices": tri.vertex_count}


def global_scale_of(k, t, hierarchy):
    """The value the game's models on a skeleton carry: one per skeleton, in all 249 that have models. The renderer doesn't apply it (docs/re/models.md 5)."""
    th = t.get("keen::RenderModel")
    for key in k.resources_of_type(th["hash"]):
        raw = k.read(key)
        if guid_text(raw[0x80:0x90]) == hierarchy:
            return struct.unpack_from("<f", raw, 0x9C)[0]
    return 1.0


def model(k, t, gltf_files, base, guid=None, mode=None, name=None, material=None, material_map=None,
          pixel_sizes=None, stem=None, subdir="cooked", target=None, global_scale=None, own_materials=None, attach=None):
    """glTF (one file per LOD, LOD 0 first) -> RenderModel .bin + renderData .blob. `base` is a game model whose materials are the defaults, or None. With `target`
    (a skin.Target) the model is skinned on that skeleton, and with `attach` too (a slot's name or j<index>) rigid on one of its joints. Morph targets named as keen::BlendShapeFlags
    become blend shapes. own_materials {"blocks", "mod"}: the mod's MaterialBlocks, which material and material_map may name.
    Returns (files {relative name: bytes}, entries, report)."""
    th = t.get("keen::RenderModel")["hash"]
    base_desc, base_key = {"materials": []}, None
    if base is not None:
        base_key = ResourceKey(base, th, 0)
        if not k.has(base_key):
            raise StepError(f"base {base} is not a keen::RenderModel of this game")
        base_desc = Decoder(t).decode(th, k.read(base_key))
    elif not guid:
        raise StepError("a model step without a base needs a guid")
    out_guid = _norm_guid(guid) if guid else base_key.guid
    mode = mode or ("override" if base_key and out_guid == base_key.guid else "new")
    if mode == "override" and not k.has(ResourceKey(out_guid, th, 0)):
        raise StepError(f"mode override: {out_guid} is not a game RenderModel")
    if mode == "new" and k.by_guid(out_guid):
        raise StepError(f"mode new: {out_guid} already names a game resource")
    docs = [gltf.read(f) for f in gltf_files]
    warnings = []
    if attach is not None and target is None:
        raise StepError(f"attach: {attach!r} is a joint of a skeleton: name it with \"skeleton\" in the step")
    joint = attach_joint(k, t, target, attach) if attach is not None else None
    for doc in docs:
        skinned = any("skin" in p for p in doc["primitives"])
        if joint is not None:
            warnings += SK.attach(doc, target, joint)
        elif target is not None:
            warnings += SK.bind(doc, target)
        elif skinned:
            raise StepError(f"{doc['file']} has a skinned mesh: name its skeleton with \"skeleton\" in the step")
    mmap = dict(material_map or {})
    slots, materials = {}, []

    def slot_for(prim):
        key = prim["material_name"] if prim["material_name"] is not None else f"#{prim['material']}"
        if key not in slots:
            g = mmap.get(key) or material or (base_desc["materials"] and base_desc["materials"][min(len(slots), len(base_desc["materials"]) - 1)]["material"])
            if not g:
                raise StepError(f"glTF material {key!r} has no game material: give \"material\", \"materialMap\" or a \"base\" model")
            slots[key] = len(materials)
            materials.append(_material_entry(k, t, g, base_desc["materials"], own_materials))
        return slots[key]

    lods = []
    for doc in docs:
        lod = []
        for prim in doc["primitives"]:
            p = RM.prepare(prim)
            p["material_slot"] = slot_for(prim)
            lod.append(p)
        lods.append(lod)
    SK.check_limits(lods, pixel_sizes, materials)
    name = name or Path(gltf_files[0]).stem
    morphs = []
    if target is not None:
        morphs, more = SK.morph_names(lods)
        warnings += more
    elif any(p.get("morphs") for lod in lods for p in lod):
        warnings.append("morph targets are left out: the game raises them on worn models only, which name a \"skeleton\"")
    skin = None
    if target is not None:
        bad_effect = [m["material"] for m in materials if m["renderEffectId"] != "Pbr"]
        if bad_effect:
            raise StepError(f"material {bad_effect[0]} has effect {next(m['renderEffectId'] for m in materials if m['material'] == bad_effect[0])}: every skinned model of the game uses Pbr")
        if global_scale is None:
            global_scale = base_desc["globalScale"] if base_desc.get("hierarchy") == target.guid else global_scale_of(k, t, target.guid) if k.by_guid(target.guid) else 1.0
        skin = {"hierarchy": target.guid, "hierarchyJointCount": len(target), "globalScale": global_scale}
    desc, blob = RM.build(lods, materials, base_desc, name, pixel_sizes, skin=skin, morphs=morphs)
    data = RM.encode(t, desc)
    stem = stem or name
    fb, fo = f"{subdir}/{stem}.bin", f"{subdir}/{stem}.blob"
    own_guids = {m["material"] for m in materials if not k.has(ResourceKey(m["material"], t.get("keen::RenderMaterialResource")["hash"], 0))} if own_materials else ()
    report = validate(k, t, data, blob, against=base_key.guid if base_key else None, gltf_docs=docs, target=target, own_materials=own_guids)
    report.update({"op": "model", "guid": out_guid, "mode": mode, "base": base_key.guid if base_key else None, "bin": fb, "blob": fo,
                   "bin_size": len(data), "blob_size": len(blob), "content_hash": desc["renderData"]["hash"],
                   "materials": materials, "warnings": warnings})
    entries = [{"kind": "blob", "file": fo},
               {"kind": "resource", "guid": out_guid, "type": "keen::RenderModel", "part": 0, "file": fb,
                "mode": mode, "patch": [{"offset": RM.RENDER_DATA_OFFSET, "blob": fo}]}]
    files = {fb: data, fo: blob}
    if target is not None:
        side = {"format": 1, "tool": "modelcook", "guid": out_guid, "name": name, "skeleton": target.guid, "joints": desc["skinningMap"], "lods": len(desc["lods"]),
                "materials": [m["material"] for m in materials], "boundingBox": desc["boundingBox"], "globalScale": desc["globalScale"], "morphs": morphs}
        if attach is not None:
            side["attach"] = str(attach)
        files[f"{subdir}/{stem}.model.json"] = json.dumps(side, indent=1).encode() + b"\n"
    return files, entries, report


def visual(k, t, base, model_guid, guid, name=None, stem=None, subdir="cooked"):
    """A new visual template: a copy of a vanilla keen::ecs::TemplateResource (an item's equipment.visualEntity)
    whose single ecs::ModelResource points at `model_guid`."""
    th = t.get("keen::ecs::TemplateResource")["hash"]
    key = ResourceKey(base, th, 0)
    if not k.has(key):
        raise StepError(f"{base} is not a keen::ecs::TemplateResource")
    guid = _norm_guid(guid)
    if k.by_guid(guid):
        raise StepError(f"{guid} already names a game resource: pick a new GUID")
    dec = Decoder(t)
    v = dec.decode(th, k.read(key))
    swapped = 0
    for c in v["components"]:
        if c and c.get("$type") == "keen::ecs::ModelResource":
            c["value"]["model"] = _norm_guid(model_guid)
            swapped += 1
    if swapped != 1:
        raise StepError(f"template {key.guid} has {swapped} ModelResource components (expected 1)")
    v["name"] = name or v["name"] + "_Kiln"
    data = Encoder(t).encode(th, v)
    ok = Encoder(t).encode(th, dec.decode(th, data)) == data
    stem = stem or v["name"]
    fb = f"{subdir}/{stem}.bin"
    report = {"op": "visual", "guid": guid, "name": v["name"], "model": _norm_guid(model_guid), "base": key.guid,
              "components": [c["$type"] for c in v["components"] if c], "bin": fb, "bin_size": len(data),
              "checks": {"reencodes_byte_exact": ok}, "ok": ok}
    entries = [{"kind": "resource", "guid": guid, "type": "keen::ecs::TemplateResource", "part": 0, "file": fb,
                "mode": "new"}]
    return {fb: data}, entries, report


def gltf_blocks(gltf_file, mod_dir, stem, out):
    """The glTF's own materials as MaterialBlocks: the file of blocks and any PNGs embedded in it, for kiln.vfx to build at boot.
    -> ({glTF material: block name}, {block name: block}, files {path relative to the mod: bytes}, warnings)."""
    try:
        names, blocks, images, warnings = MB.from_gltf(gltf_file, mod_dir, stem, out)
    except MB.MaterialError as e:
        raise StepError(str(e)) from e
    if not blocks:
        raise StepError(f"{gltf_file}: it has no materials to make blocks from")
    files = dict(images)
    files[f"blocks/{stem}.materials.json"] = MB.blocks_file(blocks, Path(gltf_file).name).encode()
    return names, blocks, files, warnings


def sha256(b):
    return hashlib.sha256(b).hexdigest()
