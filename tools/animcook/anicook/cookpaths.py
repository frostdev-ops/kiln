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

A path is judged as it is written: a ".." right after a link would climb out of wherever the link leads, so it is refused before the
path is normalised. A cook writes to the path a check returned (normalised, every component checked), never to the one it was given,
and through write(): a temporary file beside it, renamed over it, so a link put in an output's place is replaced, never followed.

receipt() records, in the game's kiln/state/cook-receipts, what one cook wrote into a mod's assets/cooked and the SHA-256 of each file,
when Kiln ran the cook (KILN_COOK_RECEIPT=1). kiln.assets loads a file of a mod's assets/cooked only when a receipt of that game lists its
bytes (docs/kiln/modding/cooking.md): a cooked file a player was given (an archive, a modpack, a copy) is never loaded, only what a cook on
this machine made from this game.
"""
import hashlib
import json
import os
import time
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


def _no_climb_out_of_a_link(target):
    """Refuses a path in which a ".." follows a link: the system resolves that ".." from where the link leads, while normalising the path
    as written drops both, so the two would name different places (cooked/link/../x is not cooked/x when link points elsewhere)."""
    raw = target if os.path.isabs(target) else os.path.join(os.getcwd(), target)
    drive, rest = os.path.splitdrive(raw)
    cur = drive + os.sep
    for part in rest.replace("\\", "/").split("/") if os.sep == "\\" else rest.split("/"):
        if part in ("", "."):
            continue
        if part == "..":
            if os.path.islink(cur):
                raise Refused(f"{raw} climbs out of the link {cur} with \"..\". A cook writes into the mod's own folders only, never through a link")
            cur = os.path.dirname(cur.rstrip("/\\")) or cur
            continue
        cur = os.path.join(cur, part)


def _under(mod, target):
    """`target` as (parts below the mod's folder, its full path); parts None when it is not below it."""
    _no_climb_out_of_a_link(target)
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


def write(path, data):
    """Writes `data` to `path` (a path a check above returned) through a temporary file beside it that is renamed over it: a link that
    took an output's place after the check is replaced, never written through, and a cook stopped halfway leaves no cut file."""
    path = Path(path)
    if path.is_symlink():
        raise Refused(f"{path} is a link. A cook writes into the mod's own folders only, never through a link")
    path.parent.mkdir(parents=True, exist_ok=True)
    for _ in range(16):
        tmp = path.parent / f".{path.name}.{os.urandom(6).hex()}.cook-tmp"
        try:
            fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0), 0o644)
        except FileExistsError:
            continue
        try:
            with os.fdopen(fd, "wb") as f:
                f.write(data)
            os.replace(tmp, path)
        except BaseException:
            try:
                os.unlink(tmp)
            except OSError:
                pass
            raise
        return path
    raise Refused(f"{path.parent}: no free temporary file name")


RECEIPTS = ("kiln", "state", "cook-receipts")


def game_dir_of(kfc_path):
    """The game folder a cook read: the folder of its archive's index (`<game>/enshrouded.kfc`)."""
    return Path(kfc_path).parent if kfc_path else None


def receipt(game, recipe_path, tool, written):
    """Records what one cook wrote into the mod's assets/cooked, in the game's kiln/state/cook-receipts (one file per mod folder, recipe
    and tool, replaced by that recipe's next cook): each file by its path below the mod's assets folder (as assets.json names it) and
    its SHA-256. kiln.assets of that game loads a cooked file only when a receipt lists its bytes; the launcher checks the files of the
    folders it cooks against them. `written`: {path a check returned: bytes}. Only a cook Kiln runs asks for it (KILN_COOK_RECEIPT=1:
    kiln cook, kiln build, the launcher); a tool run by hand writes nothing into the game's folder. Nothing is recorded for a cook that
    read no game, or a game folder without Kiln (nothing there loads the mod). -> the receipt's path, or None."""
    if game is None or os.environ.get("KILN_COOK_RECEIPT") != "1":
        return None
    kiln = Path(game) / RECEIPTS[0]
    if not kiln.is_dir() or kiln.is_symlink():
        return None
    mod = Path(mod_root(recipe_path))
    assets = mod / "assets"
    files = {}
    for f, data in written.items():
        rel = Path(os.path.normpath(os.path.abspath(f))).relative_to(os.path.normpath(os.path.abspath(assets))).as_posix()
        files[rel] = hashlib.sha256(data).hexdigest()
    try:
        recipe = Path(os.path.abspath(recipe_path)).relative_to(os.path.abspath(mod)).as_posix()
    except ValueError:
        recipe = Path(recipe_path).name
    where = os.path.normcase(os.path.normpath(os.path.abspath(mod)))
    key = hashlib.sha256(f"{where}\n{recipe}\n{tool}".encode()).hexdigest()[:24]
    folder = kiln.joinpath(*RECEIPTS[1:])
    for d in (kiln / RECEIPTS[1], folder):
        if d.is_symlink():
            raise Refused(f"{d} is a link: the cook's receipt is not written through one")
    doc = {"format": 1, "tool": tool, "mod": str(mod), "recipe": recipe, "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
           "files": dict(sorted(files.items()))}
    return write(folder / f"{key}.json", (json.dumps(doc, indent=1) + "\n").encode())
