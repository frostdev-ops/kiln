# SPDX-License-Identifier: MIT
"""Models of things a player places: a prop, a station or a piece of furniture in blocks/*.json whose look is a glTF of the mod's own.

    "props": { "lantern": { "name": "Lantern", "visual": { "model": "models/lantern.glb" } } }

A look can also be a block of the "looks" section that one of those names. Each file is cooked once, as a rigid model with no skeleton, its glTF materials made into
material blocks ("materials": "gltf"). A model's GUID follows from <mod>:model:<its path without the extension>, as a worn look's does (cook/worn.py), and
Kiln.Blocks works out the same from the block (World/PropCompiler.cs). A prop written in code is not visible here: its file needs a model step in assets/cook.json.
"""
import json
import os
from pathlib import Path

from . import material as MB, worn as W

SECTIONS = ("props", "stations", "containers", "doors", "beds", "seats")


def _gltf(f):
    return isinstance(f, str) and f.lower().endswith((".glb", ".gltf"))


def files_from_blocks(mod_dir):
    """The glTF files placed things of a mod's blocks/*.json show, each once, in file and name order. Files that aren't in the mod's folder are left out."""
    out, seen = [], set()
    docs = []
    for f in sorted((Path(mod_dir) / "blocks").glob("*.json")):
        try:
            doc = json.loads(MB._relaxed(f.read_text()))
            if isinstance(doc, dict):
                docs.append(doc)
        except ValueError:
            continue  # the block loader and kiln lint say what is wrong with it
    looks = {}
    for doc in docs:
        if isinstance(doc.get("looks"), dict):
            looks.update({k: v for k, v in doc["looks"].items() if isinstance(v, dict)})
    for doc in docs:
        for section in SECTIONS:
            for block in (doc.get(section) if isinstance(doc.get(section), dict) else {}).values():
                if not isinstance(block, dict):
                    continue
                visual = block.get("visual")
                if isinstance(visual, str):
                    visual = looks.get(visual)
                model = visual.get("model") if isinstance(visual, dict) else None
                if _gltf(model) and W._in_mod(mod_dir, model) and model not in seen:
                    seen.add(model)
                    out.append(model)
    return out


def steps_from_blocks(mod_dir, base_dir):
    """The model steps of those files, with paths relative to the recipe's folder."""
    return [{"tool": "modelcook", "op": "model", "gltf": os.path.relpath(Path(mod_dir) / f, base_dir), "lods": [], "mode": "new", "materials": "gltf",
             "name": W.name_of(f), "stem": W.stem_of(f), "fromBlocks": True, "placed": f} for f in files_from_blocks(mod_dir)]
