# SPDX-License-Identifier: MIT
"""Where a cook may write. The same file in every cook tool (modelcook/cook, animcook/anicook, soundcook/sndcook, impact/impact:
tools/modelcook/tests/test_server_cook.py holds the four copies to one text), stdlib only.

A cook writes what it makes from the game into the mod's assets/cooked folder and nowhere else: that folder is what a mod never ships
and what does not count as the mod (the mod's identity, docs/kiln/launcher/contracts.md 5.11, leaves out exactly `assets/cooked`). So
every folder and file a cook is about to write is checked here before the first one is written:

- by its spelling: below <mod>/assets/cooked with no ".." on the way, the two folders spelled exactly so (where names ignore case, as on
  Windows, "Cooked" would be made as another folder than the one the identity leaves out);
- physically: no link at any component from the mod's folder down, so a link named assets/cooked (or a link in place of an output) cannot
  send the files elsewhere. Links above the mod's folder are the system's and the player's (a Steam library reached through one).

Outside assets/cooked a cook writes two files of the mod, and only by their exact place: assets/assets.json (the manifest) and
blocks/<name>.materials.json (the material blocks made from a glTF).
"""
import os
from pathlib import Path

COOKED = ("assets", "cooked")
MANIFEST = ("assets", "assets.json")
BLOCKS = "blocks"
OUT_HINT = ' Use "out": "cooked" in assets/cook.json, or "../assets/cooked/<name>" in a recipe of another folder.'
NAME_HINT = ' Give the step a plain name or stem: no "/", no "..".'


class Refused(Exception):
    pass


def mod_root(recipe_path):
    """The folder of the mod a recipe belongs to, as the path is written (no link is followed): the nearest folder with a kiln.mod.json
    above it, or the recipe's parent's parent, as <mod>/assets/cook.json is."""
    p = Path(os.path.abspath(recipe_path))
    for d in p.parents:
        if (d / "kiln.mod.json").is_file():
            return d
    return p.parent.parent


def _under(mod, target):
    """`target` as (parts below the mod's folder, its full path); parts None when it is not below it."""
    full = os.path.normpath(os.path.abspath(target))
    for base in (os.path.normpath(os.path.abspath(mod)), os.path.realpath(mod)):
        try:
            parts = Path(full).relative_to(base).parts
        except ValueError:
            continue
        return (None if ".." in parts else parts), full
    return None, full


def _spelled(mod, parts):
    """`parts` below the mod's folder as the disk spells them (a part that is not there yet as written); refuses a link at any of them."""
    cur, out = Path(mod), []
    for part in parts:
        try:
            names = os.listdir(cur)
        except OSError:
            names = []
        actual = part
        if part not in names and os.path.lexists(cur / part):
            # names ignore case here: the entry is there under another spelling, and that spelling is the folder's
            actual = next((n for n in names if n.lower() == part.lower()), part)
        cur = cur / actual
        if os.path.islink(cur):
            raise Refused(f"{cur} is a link. A cook writes into the mod's own folders only, never through a link")
        out.append(actual)
    return out


def cooked(recipe_path, target, what, hint=NAME_HINT):
    """`target` (a folder or file the cook is about to write; relative to the recipe's folder when not absolute) lies in the mod's
    assets/cooked folder, by spelling and physically. -> its path. `what` names it in the refusal."""
    mod = mod_root(recipe_path)
    target = os.path.join(os.path.dirname(os.path.abspath(recipe_path)), target)
    parts, full = _under(mod, target)
    root = Path(mod).joinpath(*COOKED)
    outside = (f"{what} is {full}, outside the mod's assets/cooked folder ({root}). A cook writes only there: it is what a mod never ships and what "
               f"a server and a player's game each make for themselves.")
    if parts is None or len(parts) < len(COOKED):
        raise Refused(outside + hint)
    spelled = _spelled(mod, parts)
    if tuple(spelled[:2]) != COOKED:
        if tuple(s.lower() for s in spelled[:2]) == COOKED:
            outside += f" The folder is spelled {'/'.join(spelled[:2])} there: it has to be assets/cooked, in lower case (a mod's hash leaves out exactly that folder)."
        raise Refused(outside + hint)
    return Path(mod).joinpath(*spelled)


def out_dir(recipe_path, out, default):
    """A recipe's "out" (relative to the recipe): the mod's assets/cooked folder or a folder in it. -> its path."""
    if not isinstance(out, str) or not out:
        raise Refused(f"\"out\" is the folder the cook writes to, relative to this file (\"{default}\")")
    return cooked(recipe_path, out, f"\"out\": \"{out}\"", OUT_HINT)


def manifest(recipe_path, target):
    """`target` is the mod's assets/assets.json, the one manifest kiln.assets reads, and no link. -> its path."""
    mod = mod_root(recipe_path)
    target = os.path.join(os.path.dirname(os.path.abspath(recipe_path)), target)
    parts, full = _under(mod, target)
    if parts is None or tuple(_spelled(mod, parts)) != MANIFEST:
        raise Refused(f"the manifest is {full}: a cook writes its entries into the mod's assets/assets.json only ({Path(mod).joinpath(*MANIFEST)}), the file kiln.assets reads")
    return Path(mod).joinpath(*MANIFEST)


def blocks_file(recipe_path, target):
    """`target` is a file blocks/<name>.materials.json directly in the mod's blocks folder, and no link. -> its path."""
    mod = mod_root(recipe_path)
    target = os.path.join(os.path.dirname(os.path.abspath(recipe_path)), target)
    parts, full = _under(mod, target)
    spelled = _spelled(mod, parts) if parts is not None else []
    if len(spelled) != 2 or spelled[0] != BLOCKS or not spelled[1].endswith(".materials.json"):
        raise Refused(f"the material blocks of a glTF would be written to {full}: they go to blocks/<stem>.materials.json in the mod's folder ({mod}), nowhere else." + NAME_HINT)
    return Path(mod).joinpath(*spelled)
