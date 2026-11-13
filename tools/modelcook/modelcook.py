#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""modelcook: glTF 2.0 (rigid, skinned or worn) -> Keen keen::RenderModel resource + renderData blob, served by kiln.assets.

    modelcook.py recipe ASSETS/cook.json [--update-manifest] [--check] [--no-dispatch]
                                         run a mod's cook recipe (the normal way); steps of another tool
                                         ("tool": "soundcook") are handed to it (tools/soundcook) unless --no-dispatch
    modelcook.py cook IN.gltf --base GUID --assets DIR [--guid GUID] [--mode override|new] [--name DEBUGNAME] [--stem S]
                 [--material GUID | --material-map NAME=GUID ...] [--lod LOD1.gltf ...] [--pixel-size F ...] [--skeleton GUID]
    modelcook.py visual --base-template GUID --model GUID --guid GUID --assets DIR [--name NAME] [--stem S]
    modelcook.py validate DIR/cooked/NAME.bin [--blob NAME.blob] [--against GUID] [--gltf IN.gltf]
    modelcook.py looks MODDIR [--mod ID] [--update-manifest] [--check]
                                         the looks a mod's blocks/*.json name, for a mod with no assets/cook.json of modelcook's (that recipe cooks them itself)
    modelcook.py skeleton OUT.glb [--body male|female|none]
                                         the player's skeleton to weight against, from your own game, with a body to model around: it stays on your machine

A mod ships its sources (glTF, PNG, JSON) and assets/cook.json (format: cook/recipe.py, docs/kiln/modding/models.md),
never game data: the cook runs on the player's machine against the installed game (read only) and writes
assets/cooked/ (gitignored), which the mod's committed assets/assets.json names.
cook / visual are the single steps (outputs to DIR/cooked/, entries added to DIR/assets.json). Every model is
validated on the spot: decode with tools/kfc, re-encode byte-exact, geometry back within quantization error, same
structure as the base. glTF space (right-handed, +Y up, metres) becomes the game's left-handed space by mirroring x;
the model's origin is the attachment point (the hand grip for weapons).
Common options: --game DIR (default Steam's Enshrouded), --exe EXE (default the game's exe; read only).
--game may be a dedicated server's folder (enshrouded_server.kfc): recipe and looks then skip the model steps, one line each
("skipped on a dedicated server: only a game client draws it"), and cook the rest. assets.json is the same file as after a client's cook.
Everything a cook writes from the game goes into the mod's assets/cooked folder; a recipe whose "out" is elsewhere is refused.
"""
import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "kfc"))

from cook import gltf, recipe, skin, steps  # noqa: E402
from cook.validate import validate  # noqa: E402
from kfc.archive import Kfc  # noqa: E402
from kfc.cli import DEFAULT_GAME, _exe_for  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402


def _ctx(a):
    game = Path(a.game).expanduser()
    k = Kfc(game)
    exe = Path(a.exe).expanduser() if a.exe else _exe_for(game)
    t = TypeDb.from_exe(exe)
    return k, t


def _write(assets_dir, files, entries):
    adir = Path(assets_dir)
    for rel, data in files.items():
        (adir / rel).parent.mkdir(parents=True, exist_ok=True)
        (adir / rel).write_bytes(data)
    mp = adir / "assets.json"
    man = json.loads(mp.read_text()) if mp.exists() else {"format": 1, "entries": []}
    names = {(e["kind"], e["file"]) for e in entries}
    man["entries"] = [e for e in man["entries"] if (e.get("kind"), e.get("file")) not in names] + entries
    mp.write_text(json.dumps(man, indent=2) + "\n")


DISPATCH = {"soundcook": HERE.parent / "soundcook" / "soundcook.py", "animcook": HERE.parent / "animcook" / "animcook.py"}


def _dispatch(a, doc, extra):
    """Run the steps of the other tools named in the recipe (one cook.json for models and sounds)."""
    import subprocess
    rc = 0
    for tool in recipe.other_tools(doc):
        script = DISPATCH.get(tool)
        if script is None or not script.exists():
            print(f"modelcook: steps of tool {tool!r}: no runner found", file=sys.stderr)
            rc = 1
            continue
        cmd = [sys.executable] + (["-I"] if sys.flags.isolated else []) + [str(script), "--game", str(a.game)] + (["--exe", a.exe] if a.exe else []) + \
              ["recipe", a.recipe, "--no-dispatch"] + extra
        print(f"modelcook: {tool} steps -> {' '.join(cmd[1:])}", file=sys.stderr)
        rc = subprocess.run(cmd).returncode or rc
    return rc


def _say_steps(r):
    """One line per step of a recipe's run; a step skipped on a dedicated server says so."""
    for s in r["steps"]:
        if s.get("skipped"):
            print(f"step {s['step']} {s['op']} {s['name']}: {s['skipped']}")
            continue
        print(f"step {s['step']} {s['op']}: {s['guid']} {'ok' if s['ok'] else 'FAILED'}"
              + ("" if s["ok"] else " " + json.dumps({c: v for c, v in s["checks"].items() if not v})))


def _say_marks(r):
    """The "side": "client" marks a server's cook of 0.2.0-dev.26 wrote on the models' entries: taken out, or still there."""
    def n(files):
        return f"{len(files)} entr{'y' if len(files) == 1 else 'ies'}"
    gone, there = r["manifest"].get("unmarked"), r["manifest"].get("marked")
    if gone:
        print(f"assets.json: took the \"side\": \"client\" mark of an older cook out of {n(gone)} (a server skips a model by its type; with the mark "
              "this copy was another file than the players'): " + ", ".join(gone))
    if there and r["manifest"].get("legacyMarks"):
        print(f"assets.json: {n(there)} of the models carry a \"side\": \"client\" mark the cook of 0.2.0-dev.26 wrote on this server, so this copy "
              "differs from the players' (run with --update-manifest to take it out): " + ", ".join(there))
    elif there:
        print(f"assets.json: {n(there)} of the models carry a \"side\": \"client\" mark. Nothing here says a cook wrote it, so it stays as the mod has "
              "it. If a cook of 0.2.0-dev.26 wrote it on this server, this copy differs from the players': put the mod's own assets/assets.json back. "
              + ", ".join(there))


def cmd_recipe(a):
    doc = recipe.load(a.recipe)
    recipe.out_dir(a.recipe, doc)  # before anything is read or written: a cook writes into the mod's assets/cooked only
    others = [] if a.no_dispatch else recipe.other_tools(doc)
    if a.check:
        need = recipe.needs_cook(a.recipe)
        rc = _dispatch(a, doc, ["--check"]) if others else 0
        print("cook needed" if need else "up to date")
        return 1 if need or rc else 0
    if not recipe.own_steps(doc):
        return _dispatch(a, doc, ["--update-manifest"] if a.update_manifest else []) if others else 0
    k, t = _ctx(a)
    r = recipe.run(a.recipe, k, t, exe_sha256=t.dump.get("exe_sha256"), update_manifest=a.update_manifest)
    _say_steps(r)
    if r["manifest"]["missing"] or r["manifest"]["mismatched"]:
        print("assets.json does not match the cooked files (run with --update-manifest):")
        for e in r["manifest"]["missing"] + r["manifest"]["mismatched"]:
            print("  " + json.dumps(e))
    _say_marks(r)
    print(f"{'ok' if r['ok'] else 'FAILED'}: {len(r['outputs'])} file(s), report {Path(a.recipe).parent / doc.get('out', 'cooked') / recipe.report_name(doc)}")
    rc = _dispatch(a, doc, ["--update-manifest"] if a.update_manifest else []) if others else 0
    return 0 if r["ok"] and not rc else 1


def cmd_cook(a):
    k, t = _ctx(a)
    mm = dict(x.split("=", 1) for x in (a.material_map or []))
    files, entries, r = steps.model(k, t, [a.input] + (a.lod or []), a.base, guid=a.guid, mode=a.mode, name=a.name,
                                    material=a.material, material_map=mm, pixel_sizes=a.pixel_size, stem=a.stem,
                                    target=steps.game_target(k, t, a.skeleton) if a.skeleton else None, global_scale=a.global_scale)
    _write(a.assets, files, entries)
    (Path(a.assets) / "cooked" / f"{Path(r['bin']).stem}.report.json").write_text(json.dumps(r, indent=2) + "\n")
    print(json.dumps({x: r[x] for x in ("guid", "mode", "bin", "bin_size", "blob_size", "ok", "checks")}, indent=2))
    return 0 if r["ok"] else 1


def cmd_visual(a):
    k, t = _ctx(a)
    files, entries, r = steps.visual(k, t, a.base_template, a.model, a.guid, name=a.name, stem=a.stem)
    _write(a.assets, files, entries)
    print(json.dumps(r, indent=2))
    return 0 if r["ok"] else 1


def cmd_validate(a):
    k, t = _ctx(a)
    data = Path(a.bin).read_bytes()
    blob = Path(a.blob).read_bytes() if a.blob else Path(a.bin).with_suffix(".blob").read_bytes()
    docs = [gltf.read(a.gltf)] if a.gltf else None
    if docs and any("skin" in p for p in docs[0]["primitives"]):
        print("modelcook: a skinned glTF is compared with the model when it is cooked, not here (its bind pose depends on the skeleton)", file=sys.stderr)
        docs = None
    r = validate(k, t, data, blob, against=a.against, gltf_docs=docs)
    print(json.dumps(r, indent=2))
    return 0 if r["ok"] else 1


def cmd_looks(a):
    mod_dir = Path(a.mod_dir)
    real = mod_dir / "assets" / "cook.json"
    if real.exists() and recipe.load(real).get("tool", recipe.TOOL) == recipe.TOOL:
        print(f"{real} cooks the looks: modelcook.py recipe {real}")
        return 0
    path, doc = recipe.looks_recipe(mod_dir, a.mod)
    # the steps name the mod's files from assets/, which a mod with only blocks may not have yet
    path.parent.mkdir(parents=True, exist_ok=True)
    if not recipe.own_steps(recipe.load(path, doc)):
        print("no looks")
        return 0
    if a.check:
        need = recipe.needs_cook(path, doc)
        print("cook needed" if need else "up to date")
        return 1 if need else 0
    k, t = _ctx(a)
    r = recipe.run(path, k, t, exe_sha256=t.dump.get("exe_sha256"), update_manifest=a.update_manifest, given=doc)
    _say_steps(r)
    if r["manifest"]["missing"] or r["manifest"]["mismatched"]:
        print("assets.json does not match the cooked files (run with --update-manifest)")
    _say_marks(r)
    print(f"{'ok' if r['ok'] else 'FAILED'}: {len(r['outputs'])} file(s), report {path.parent / doc['out'] / recipe.LOOKS_REPORT}")
    return 0 if r["ok"] else 1


def cmd_skeleton(a):
    k, t = _ctx(a)
    r = steps.player_reference(k, t, Path(a.out), None if a.body == "none" else a.body)
    print(json.dumps({"file": a.out, **r}, indent=1))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(prog="modelcook.py", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=str(DEFAULT_GAME))
    ap.add_argument("--exe")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("recipe")
    p.add_argument("recipe")
    p.add_argument("--update-manifest", action="store_true", help="add/replace the cooked entries in assets.json")
    p.add_argument("--check", action="store_true", help="only say whether a cook is needed (no game access)")
    p.add_argument("--no-dispatch", action="store_true", help="run only modelcook's steps (no other tool's)")
    p = sub.add_parser("cook")
    p.add_argument("input")
    p.add_argument("--base", required=True)
    p.add_argument("--skeleton", help="a game skeleton's GUID: the glTF's skin is bound to it")
    p.add_argument("--global-scale", type=float, help="the value to store (default: what the game's models on this skeleton store)")
    p.add_argument("--assets", required=True, help="the mod's assets/ folder")
    p.add_argument("--guid")
    p.add_argument("--mode", choices=("override", "new"))
    p.add_argument("--name")
    p.add_argument("--stem")
    p.add_argument("--material")
    p.add_argument("--material-map", action="append")
    p.add_argument("--lod", action="append", help="extra LOD glTF files, coarser each")
    p.add_argument("--pixel-size", type=float, action="append", help="per LOD (LOD 0 = 0)")
    p = sub.add_parser("visual")
    p.add_argument("--base-template", required=True, help="the base item's equipment.visualEntity")
    p.add_argument("--model", required=True, help="the new RenderModel GUID")
    p.add_argument("--guid", required=True, help="the new template's GUID (fixed forever)")
    p.add_argument("--name")
    p.add_argument("--stem")
    p.add_argument("--assets", required=True)
    p = sub.add_parser("validate")
    p.add_argument("bin")
    p.add_argument("--blob")
    p.add_argument("--against")
    p.add_argument("--gltf")
    p = sub.add_parser("looks")
    p.add_argument("mod_dir")
    p.add_argument("--mod", help="the mod's id, when it has no kiln.mod.json in its folder")
    p.add_argument("--update-manifest", action="store_true")
    p.add_argument("--check", action="store_true", help="only say whether a cook is needed (no game access)")
    p = sub.add_parser("skeleton")
    p.add_argument("out")
    p.add_argument("--body", choices=("male", "female", "none"), default="male", help="the body to model around, a mesh the cook leaves out (default: male)")
    a = ap.parse_args(argv)
    try:
        return {"recipe": cmd_recipe, "cook": cmd_cook, "visual": cmd_visual, "validate": cmd_validate, "skeleton": cmd_skeleton, "looks": cmd_looks}[a.cmd](a)
    except (steps.StepError, recipe.RecipeError, steps.RM.CookError, gltf.GltfError, skin.SkinError) as e:
        print(f"modelcook: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
