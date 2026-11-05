# SPDX-License-Identifier: MIT
"""The cook steps behind the CLI and cook.json recipes. Each step reads vanilla inputs from the installed game (read
only) and returns the bytes it produced plus the kiln.assets entries that serve them (file names relative to the
mod's assets/ folder)."""
import hashlib
import json
import struct
from pathlib import Path

from kfc.archive import ResourceKey
from kfc.blob import Decoder
from kfc.blobwrite import Encoder
from kfc.util import guid_bytes, guid_text

from . import gltf, rendermodel as RM, skin as SK
from .validate import validate


class StepError(Exception):
    pass


def _norm_guid(g):
    return guid_text(guid_bytes(g))


def _material_entry(k, t, guid, base_materials):
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


def global_scale_of(k, t, hierarchy):
    """The value the game's models on a skeleton carry: one per skeleton, in all 249 that have models. The renderer doesn't apply it (docs/re/models.md 5)."""
    th = t.get("keen::RenderModel")
    for key in k.resources_of_type(th["hash"]):
        raw = k.read(key)
        if guid_text(raw[0x80:0x90]) == hierarchy:
            return struct.unpack_from("<f", raw, 0x9C)[0]
    return 1.0


def model(k, t, gltf_files, base, guid=None, mode=None, name=None, material=None, material_map=None,
          pixel_sizes=None, stem=None, subdir="cooked", target=None, global_scale=None):
    """glTF (one file per LOD, LOD 0 first) -> RenderModel .bin + renderData .blob. `base` is a game model whose materials are the defaults, or None. With `target`
    (a skin.Target) the model is skinned on that skeleton. Returns (files {relative name: bytes}, entries, report)."""
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
    for doc in docs:
        skinned = any("skin" in p for p in doc["primitives"])
        if target is not None:
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
            materials.append(_material_entry(k, t, g, base_desc["materials"]))
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
    skin = None
    if target is not None:
        bad_effect = [m["material"] for m in materials if m["renderEffectId"] != "Pbr"]
        if bad_effect:
            raise StepError(f"material {bad_effect[0]} has effect {next(m['renderEffectId'] for m in materials if m['material'] == bad_effect[0])}: every skinned model of the game uses Pbr")
        if global_scale is None:
            global_scale = base_desc["globalScale"] if base_desc.get("hierarchy") == target.guid else global_scale_of(k, t, target.guid) if k.by_guid(target.guid) else 1.0
        skin = {"hierarchy": target.guid, "hierarchyJointCount": len(target), "globalScale": global_scale}
    desc, blob = RM.build(lods, materials, base_desc, name, pixel_sizes, skin=skin)
    data = RM.encode(t, desc)
    stem = stem or name
    fb, fo = f"{subdir}/{stem}.bin", f"{subdir}/{stem}.blob"
    report = validate(k, t, data, blob, against=base_key.guid if base_key else None, gltf_docs=docs, target=target)
    report.update({"op": "model", "guid": out_guid, "mode": mode, "base": base_key.guid if base_key else None, "bin": fb, "blob": fo,
                   "bin_size": len(data), "blob_size": len(blob), "content_hash": desc["renderData"]["hash"],
                   "materials": materials, "warnings": warnings})
    entries = [{"kind": "blob", "file": fo},
               {"kind": "resource", "guid": out_guid, "type": "keen::RenderModel", "part": 0, "file": fb,
                "mode": mode, "patch": [{"offset": RM.RENDER_DATA_OFFSET, "blob": fo}]}]
    files = {fb: data, fo: blob}
    if target is not None:
        side = {"format": 1, "tool": "modelcook", "guid": out_guid, "name": name, "skeleton": target.guid, "joints": desc["skinningMap"], "lods": len(desc["lods"]),
                "materials": [m["material"] for m in materials], "boundingBox": desc["boundingBox"], "globalScale": desc["globalScale"]}
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


def sha256(b):
    return hashlib.sha256(b).hexdigest()
