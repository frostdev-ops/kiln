# SPDX-License-Identifier: MIT
"""Worn models: a look on the player's body. A WornLook in blocks/*.json is all a mod needs: the cook finds its files there (the "wornLooks" section, a cosmetic's or a piece of armor's look, and an armor set's
slots) and cooks each for the player's skeleton. A look written in code is not visible here, so its files are named by a "worn" step in assets/cook.json:

    { "op": "worn", "model": "models/frost-hood.glb", "attach": "Head", "lods": ["models/frost-hood_lod1.glb"], "pixelSizes": [0, 0.05], "materials": { "cloth": "frost_cloth" } }

`model` and `lods` are relative to the mod's folder, as in a block. `attach` makes a rigid model of a mesh with no skin: every vertex on that joint, the file's origin at the joint. `materials`
maps a glTF material to a game material's GUID or a MaterialBlock's name, and "*" is every material; a glTF material it leaves out becomes a block of its own ("materials": "gltf" in a model step).

A model's GUID follows from <mod>:model:<its path without the extension>, and Kiln.Blocks works out the same from the look (Items/CosmeticCompiler.cs). A second body is another file, so it has
another GUID: an item's `equipment.visualModels` names it.
"""
import json
import os
from pathlib import Path

from . import material as MB

LOOK_KEYS = ("attach", "lods", "pixelSizes", "materials")


def name_of(path):
    """What a model's GUID follows from: its path in the mod, without the extension."""
    p = Path(path).as_posix()
    return p[:p.rfind(".")] if "." in Path(p).name else p


def stem_of(path):
    """What its cooked files are called."""
    return name_of(path).replace("/", "_")


def _files(look):
    """The file of a look, its second body and its variants. In JSON a look's fields are as C# names them, in camel case."""
    for f in (look.get("model"), look.get("female"), *(look.get("variants") or {}).values()):
        if isinstance(f, str) and f.lower().endswith((".glb", ".gltf")):
            yield f


def steps_of_look(look):
    """The worn steps a look asks for. Its lods, attach and materials are its model's; another body takes attach and materials, the rest of it has one level."""
    out = []
    for i, f in enumerate(_files(look)):
        st = {"op": "worn", "model": f, "fromBlocks": True}
        for k in LOOK_KEYS:
            if k in look and (k not in ("lods", "pixelSizes") or (i == 0 and f == look.get("model"))):
                st[k] = look[k]
        out.append(st)
    return out


def _in_mod(mod_dir, f):
    """A file of the mod, there to read. One that isn't is the block's mistake, which the block loader and kiln lint name: the cook leaves it out rather than stop on it."""
    root = Path(mod_dir).resolve()
    p = (root / f).resolve()
    return p.is_relative_to(root) and p.is_file()


def steps_from_blocks(mod_dir):
    """The worn steps of every look in a mod's blocks/*.json, each file once, in file and name order. Files that aren't in the mod's folder are left out."""
    out, seen = [], set()
    for f in sorted((Path(mod_dir) / "blocks").glob("*.json")):
        try:
            doc = json.loads(MB._relaxed(f.read_text()))
        except ValueError:
            continue  # the block loader and kiln lint say what is wrong with it
        looks = list((doc.get("wornLooks") or {}).values())
        for section in ("cosmetics", "armor", "hair", "beards"):
            looks += [c["look"] for c in (doc.get(section) or {}).values() if isinstance(c, dict) and isinstance(c.get("look"), dict)]
        looks += [v for st in (doc.get("armorSets") or {}).values() if isinstance(st, dict) for v in st.values() if isinstance(v, dict)]
        for look in looks:
            if not isinstance(look, dict):
                continue
            for st in steps_of_look(look):
                st["lods"] = [f for f in st.get("lods", []) if _in_mod(mod_dir, f)] if "lods" in st else st.get("lods")
                if st.get("lods") is None:
                    st.pop("lods", None)
                if not _in_mod(mod_dir, st["model"]):
                    continue
                if st["model"] not in seen:
                    seen.add(st["model"])
                    out.append(st)
    return out


def _gltf_materials(path):
    from . import gltf
    doc, _ = gltf._load(path)
    return [m.get("name", f"material{i}") for i, m in enumerate(doc.get("materials", []))]


def as_model_step(st, base_dir, mod_dir):
    """A worn step as the model step it runs."""
    rel = lambda f: os.path.relpath(Path(mod_dir) / f, base_dir)  # noqa: E731
    out = {"op": "model", "gltf": rel(st["model"]), "lods": [rel(f) for f in st.get("lods", [])], "skeleton": st.get("skeleton", "@player"),
           "name": name_of(st["model"]), "stem": stem_of(st["model"]), "mode": "new"}
    for k in ("pixelSizes", "attach", "globalScale"):
        if k in st:
            out[k] = st[k]
    mapping = dict(st.get("materials") or {})
    if "*" in mapping:
        out["material"] = mapping.pop("*")
    if mapping:
        out["materialMap"] = mapping
    if "material" not in out:
        file = Path(mod_dir) / st["model"]
        if not file.exists() or any(m not in mapping for m in _gltf_materials(file)):
            out["materials"] = "gltf"
    return out
