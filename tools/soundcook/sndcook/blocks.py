# SPDX-License-Identifier: MIT
"""Sounds a mod declares as blocks, in the "sounds" section of its blocks/*.json:

    "sounds": {
      "frost-hum": { "clips": ["audio/frost_hum.wav"], "volume": 0.6, "range": [3, 40], "loop": true },
      "boom":      { "clips": ["audio/boom_1.wav", "audio/boom_2.ogg"], "range": [5, 80], "pitchRandomness": 0.05 }
    }

`soundcook.py blocks MODDIR` writes the steps they stand for into the mod's assets/cook.json, which is then cooked as any recipe: a clip is a `sound`
step `<block>.<n>`, the block is a `cue` step `<block>` that picks one of its clips at random. Steps it wrote are listed in the recipe's
"blockSounds", so a later run replaces them and leaves the author's own steps alone. A recipe it makes has the guidNamespace Kiln.Blocks expects for
a mod with no recipe of its own (UUIDv5 of the blocks namespace and "<mod>:sounds"); an existing recipe keeps its own, and Kiln.Blocks reads it there.
A block written in code is not visible here: Kiln.Blocks names the steps such a block needs (managed/services/Kiln.Blocks/SoundBlocks.cs).
"""
import json
import os
import re
import uuid
from pathlib import Path

BLOCK_NS = uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77")  # Kiln.Blocks' BlockIds
CUE_COPY = ("volume", "loop", "scene", "pitch", "pitchRandomness", "volumeRandomness", "maxCount", "priority")
AUDIO = (".wav", ".flac", ".ogg", ".mp3")


class BlocksError(Exception):
    pass


def _relaxed(text):
    """JSON with // comments and trailing commas, as the block loader reads it."""
    text = re.sub(r"(?m)^\s*//.*$", "", text)
    text = re.sub(r'("(?:\\.|[^"\\])*")|//[^\n]*|/\*.*?\*/', lambda m: m.group(1) or "", text, flags=re.S)
    return re.sub(r",(\s*[}\]])", r"\1", text)


def mod_id(mod_dir):
    try:
        return json.loads(_relaxed((Path(mod_dir) / "kiln.mod.json").read_text()))["id"]
    except (OSError, ValueError, KeyError):
        return Path(mod_dir).name


def derived_namespace(mod):
    """The guidNamespace of a recipe this writes for a mod: what Kiln.Blocks derives for a mod with no recipe of its own."""
    return str(uuid.uuid5(BLOCK_NS, f"{mod}:sounds"))


def read_blocks(mod_dir):
    """{block name: block} of every blocks/*.json "sounds" section, in file order."""
    out = {}
    for f in sorted((Path(mod_dir) / "blocks").glob("*.json")):
        try:
            doc = json.loads(_relaxed(f.read_text()))
        except ValueError:
            continue  # the block loader and kiln lint say what is wrong with it
        for name, b in (doc.get("sounds") or {}).items():
            if isinstance(b, dict):
                out[name] = b
    return out


def steps_of(name, b, mod_dir, base_dir):
    """The recipe steps one block stands for."""
    clips = b.get("clips")
    if isinstance(clips, str):
        clips = [clips]
    if not isinstance(clips, list) or not clips or not all(isinstance(c, str) for c in clips):
        raise BlocksError(f"sounds.{name}.clips: one or more audio files in the mod's folder")
    root = Path(mod_dir).resolve()
    rng = b.get("range")
    positional = isinstance(rng, list) and len(rng) == 2
    steps, takes = [], []
    for i, clip in enumerate(clips, 1):
        f = (root / clip).resolve()
        if not f.is_relative_to(root) or not clip.lower().endswith(AUDIO):
            raise BlocksError(f"sounds.{name}.clips[{i - 1}]: {clip!r} is not an audio file in the mod's folder (wav, flac, ogg, mp3)")
        st = {"op": "sound", "name": f"{name}.{i}", "src": os.path.relpath(f, base_dir).replace(os.sep, "/"), "channels": b.get("channels", "mono" if positional else "keep")}
        for k in ("normalize", "gain"):
            if k in b:
                st[k] = b[k]
        steps.append(st)
        takes.append(st["name"])
    cue = {"op": "cue", "name": name, "sounds": takes}
    if positional:
        cue["positional"] = {"min": rng[0], "max": rng[1]}
    for k in CUE_COPY:
        if k in b:
            cue[k] = b[k]
    steps.append(cue)
    return steps


def write(mod_dir, check=False):
    """Writes (or with check, compares) the block sounds' steps into assets/cook.json. Returns (changed, recipe path, the recipe, block names)."""
    mod_dir = Path(mod_dir)
    blocks = read_blocks(mod_dir)
    path = mod_dir / "assets" / "cook.json"
    if path.exists():
        doc = json.loads(path.read_text())
    elif not blocks:
        return False, path, None, []
    else:
        doc = {"format": 1, "tool": "soundcook", "out": "cooked", "guidNamespace": derived_namespace(mod_id(mod_dir)), "steps": []}
    sound_tool = doc.get("tool", "modelcook") == "soundcook"
    owned = set(doc.get("blockSounds", []))
    keep = [st for st in doc.get("steps", []) if not ((st.get("tool", doc.get("tool", "modelcook")) == "soundcook") and st.get("name") in owned)]
    taken = {(st.get("op"), st.get("name")) for st in keep}
    new, names = [], []
    for name, b in blocks.items():
        for st in steps_of(name, b, mod_dir, path.parent):
            if (st["op"], st["name"]) in taken:
                raise BlocksError(f"sounds.{name}: assets/cook.json already has a {st['op']} step {st['name']!r} of its own: rename the block or the step")
            if not sound_tool:
                st = {"tool": "soundcook", **st}
            new.append(st)
            names.append(st["name"])
    if not doc.get("guidNamespace") and new:
        doc["guidNamespace"] = derived_namespace(mod_id(mod_dir))
    out = dict(doc)
    out["steps"] = keep + new
    if names:
        out["blockSounds"] = names
    else:
        out.pop("blockSounds", None)
    changed = json.dumps(out, sort_keys=True) != json.dumps(doc, sort_keys=True) or not path.exists()
    if changed and not check:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(out, indent=2) + "\n")
    return changed, path, out, list(blocks)
