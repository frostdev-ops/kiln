#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""animcook: glTF animation -> the game's skeleton, clips, root motion, events and animation graph, served by kiln.assets.

    animcook.py recipe ASSETS/cook.json [--update-manifest] [--check] [--no-dispatch]
                                          run a mod's cook recipe (the normal way; docs/kiln/modding/animations.md). Steps of
                                          another tool in the same recipe are handed to it unless --no-dispatch.
    animcook.py info FILE.glb             what the file holds: skeleton, animations, lengths
    animcook.py clip FILE.bin             one cooked clip: frames, segments, tracks, keys, error-free rebuild
    animcook.py gen-types                 refresh types/anim-types.json from the installed game's exe (maintainers)

No game install is needed unless a step names a game input (a game skeleton, an override): a cook of your own glTF uses the bundled
reflection. With a game, its reflection is used and compared with the bundled one, so a game update that changes a format stops here.
Common options: --game DIR (default Steam's Enshrouded), --exe EXE (read only), --no-game.
"""
import argparse
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "kfc"))
sys.path.insert(0, str(HERE.parent / "modelcook"))

from cook.gltf import GltfError  # noqa: E402
from kfc import anim  # noqa: E402
from kfc.archive import Kfc  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

from anicook import clips as C, graphgen as G, recipe, skeleton as K  # noqa: E402
from anicook.gltfdoc import Doc  # noqa: E402

DEFAULT_GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
TYPES_FILE = HERE / "types" / "anim-types.json"
DISPATCH = {"modelcook": HERE.parent / "modelcook" / "modelcook.py", "soundcook": HERE.parent / "soundcook" / "soundcook.py"}
NODE_PREFIX = "keen::anim_graph::runtime_graph::"
ROOTS = list(recipe.USED_TYPES) + ["keen::SfxNotifierEvent", "keen::VfxNotifierEvent", "keen::LeftFootPlantingEvent", "keen::RightFootPlantingEvent",
                                   "keen::actor::TriggerNoiseEvent"]


def _exe_of(game):
    for n in ("enshrouded.exe", "enshrouded_server.exe"):
        if (game / n).exists():
            return game / n
    return None


def _types(a):
    """-> (TypeDb, Kfc or None, info). The game's reflection when there is a game, else the bundled one."""
    bundled = TypeDb.load(TYPES_FILE)
    if a.no_game:
        return bundled, None, {"source": "bundled"}
    game = Path(a.game).expanduser()
    exe = Path(a.exe).expanduser() if a.exe else _exe_of(game)
    if exe is None or not exe.exists():
        return bundled, None, {"source": "bundled"}
    t = TypeDb.from_exe(exe)
    changed = [n for n in recipe.USED_TYPES if t.get(n) is None or t.get(n)["layout"] != bundled.get(n)["layout"]]
    if changed:
        print(f"animcook: the game changed {', '.join(changed)}: its layout differs from the one this tool was checked on", file=sys.stderr)
    k = Kfc(game) if (game / "enshrouded.kfc").exists() or (game / "enshrouded_server.kfc").exists() else None
    return t, k, {"source": "game", "exe_sha256": t.dump.get("exe_sha256"), "changed": changed}


def _dispatch(a, doc, extra):
    rc = 0
    for tool in recipe.other_tools(doc):
        script = DISPATCH.get(tool)
        if script is None or not script.exists():
            print(f"animcook: steps of tool {tool!r}: no runner found", file=sys.stderr)
            rc = 1
            continue
        cmd = [sys.executable, str(script), "--game", str(a.game)] + (["--exe", a.exe] if a.exe else []) + ["recipe", a.recipe, "--no-dispatch"] + extra
        rc = subprocess.run(cmd).returncode or rc
    return rc


def cmd_recipe(a):
    doc = recipe.load(a.recipe)
    others = [] if a.no_dispatch else recipe.other_tools(doc)
    if a.check:
        need = recipe.needs_cook(a.recipe)
        rc = _dispatch(a, doc, ["--check"]) if others else 0
        print("cook needed" if need else "up to date")
        return 1 if need or rc else 0
    if not recipe.own_steps(doc):
        return _dispatch(a, doc, ["--update-manifest"] if a.update_manifest else []) if others else 0
    t, k, tinfo = _types(a)
    r = recipe.Run(a.recipe, t, k).run(update_manifest=a.update_manifest, game_info={"version": k.version} if k else None, types_info=tinfo)
    for s in r["steps"]:
        if s["op"] == "clips":
            for c in s["clips"]:
                e = c["errors"]
                print(f"clip {c['name']}: {c['frames']} frames, {c['bytes']} B, {c['keys']} keys, max error {e['rotationDeg']:.3f} deg {e['position'] * 1000:.2f} mm"
                      + ("" if c["ok"] else " TOO LARGE"))
                for w in c["warnings"]:
                    print(f"  warning: {w}")
        else:
            print(f"{s['op']} {s['name']}: {s['guid']} {s['bytes']} B" + (f", {s['states']} states, {s['nodes']} nodes" + (", nested" if s.get("nested") else "") if s["op"] == "graph" else f", {s['joints']} joints"))
            for w in s.get("warnings", []):
                print(f"  warning: {w}")
    if r["manifest"]["missing"] or r["manifest"]["mismatched"]:
        print("assets.json does not match the cooked files (run with --update-manifest):")
        for e in r["manifest"]["missing"] + r["manifest"]["mismatched"]:
            print("  " + json.dumps(e))
    print(f"{'ok' if r['ok'] else 'FAILED'}: {len(r['outputs'])} file(s)")
    rc = _dispatch(a, doc, ["--update-manifest"] if a.update_manifest else []) if others else 0
    return 0 if r["ok"] and not rc else 1


def cmd_info(a):
    doc = Doc(a.file)
    try:
        sk = K.from_gltf(doc)
        print(f"skeleton: {len(sk)} joints: " + ", ".join(sk.names[:24]) + (" ..." if len(sk) > 24 else ""))
    except K.SkeletonError as e:
        print(f"skeleton: {e}")
    for name, chans in doc.animations().items():
        times = [t for c in chans.values() for ch in c.values() for t in (ch.times[0], ch.times[-1])]
        kinds = sorted({ch.interp for c in chans.values() for ch in c.values()})
        print(f"animation {name!r}: {max(times) - min(times):.3f} s, {len(chans)} joints, {'/'.join(kinds)}" if times else f"animation {name!r}: empty")
    return 0


def cmd_clip(a):
    raw = Path(a.file).read_bytes()
    c = anim.parse(raw)
    tracks = sum(len(s.orientation.animated) + len(s.position.animated) + len(s.scale.animated) for s in c.segments)
    keys = sum(len(t.keys) for s in c.segments for ch in (s.orientation, s.position, s.scale) for t in ch.animated)
    print(f"{len(raw)} bytes, {c.frame_count} frames at {c.fps} fps, {c.joint_count} joints, {'loop' if c.flags == 1 else 'clamp'}, {len(c.segments)} segments, "
          f"{tracks} animated tracks, {keys} keys; rebuilds byte-identical: {anim.build(c) == raw}")
    return 0


def cmd_gen_types(a):
    exe = Path(a.exe).expanduser() if a.exe else _exe_of(Path(a.game).expanduser())
    t = TypeDb.from_exe(exe)
    names = ROOTS + sorted(x["name"] for x in t.dump["types"] if x["name"].startswith(NODE_PREFIX) and x["name"].endswith("NodeDefinition"))
    d = t.closure(names)
    TYPES_FILE.parent.mkdir(parents=True, exist_ok=True)
    TYPES_FILE.write_text(json.dumps(d, separators=(",", ":")))
    print(f"{TYPES_FILE}: {d['count']} types, {TYPES_FILE.stat().st_size} B")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(prog="animcook.py", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=str(DEFAULT_GAME))
    ap.add_argument("--exe")
    ap.add_argument("--no-game", action="store_true")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("recipe")
    p.add_argument("recipe")
    p.add_argument("--update-manifest", action="store_true")
    p.add_argument("--check", action="store_true")
    p.add_argument("--no-dispatch", action="store_true")
    p = sub.add_parser("info")
    p.add_argument("file")
    p = sub.add_parser("clip")
    p.add_argument("file")
    sub.add_parser("gen-types")
    a = ap.parse_args(argv)
    try:
        return {"recipe": cmd_recipe, "info": cmd_info, "clip": cmd_clip, "gen-types": cmd_gen_types}[a.cmd](a)
    except (recipe.RecipeError, K.SkeletonError, C.ClipError, G.GraphError, GltfError) as e:
        print(f"animcook: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
