#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Cook a creature, export the cooked skeleton and clips back to glTF, and let Blender compare them with the source (blender_check.py). Needs `blender` on PATH; no game.
Two sources: the sample golem, written by make_golem.py; and a creature authored in Blender and exported by its own glTF exporter (blender_author.py).

    python3 tools/animcook/tests/run_blender_check.py [WORK_DIR]
"""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path[:0] = [str(ROOT), str(ROOT / "samples"), str(ROOT.parent / "kfc"), str(ROOT.parent / "modelcook")]

import make_golem  # noqa: E402
from anicook import recipe as R  # noqa: E402
from kfc import anim  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

work_root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(tempfile.mkdtemp())


def blender(script, *args):
    r = subprocess.run(["blender", "-b", "--factory-startup", "--python", str(HERE / script), "--", *map(str, args)], capture_output=True, text=True, timeout=500)
    lines = [x for x in r.stdout.splitlines() if x.startswith("BLENDER") or "Error" in x or "Traceback" in x]
    return lines or (r.stdout[-2000:] + r.stderr[-2000:]).splitlines()


def check(title, work, source, clips):
    print(f"== {title}")
    (work / "cook.json").write_text(json.dumps({"format": 1, "tool": "animcook", "mod": "check", "steps": [
        {"op": "skeleton", "name": "g", "gltf": source.name},
        {"op": "clips", "skeleton": "g", "gltf": source.name, "clips": {k: {"animation": a, "loop": l} for k, (a, l) in clips.items()}}]}))
    types = TypeDb.load(ROOT / "types" / "anim-types.json")
    rep = R.Run(work / "cook.json", types).run(update_manifest=True)
    assert rep["ok"], rep
    hraw = (work / "cooked/g.skeleton.bin").read_bytes()
    h = Decoder(types, max_array=1 << 20).decode(types.get(R.HIER), hraw)
    dp = anim.parse_default_pose(hraw)
    side = json.loads((work / "cooked/g.skeleton.json").read_text())
    print("  skeleton:", " ".join(f"{j['name']}<{side['joints'][j['parent']]['name'] if j['parent'] is not None else '-'}" for j in side["joints"]))
    pairs = []
    for k, (a, _) in clips.items():
        c = anim.parse((work / f"cooked/clip_{k}.bin").read_bytes())
        out = work / f"cooked_{k}.gltf"
        anim.write_gltf(c, dp, h, out)
        pairs.append({"animation": a, "file": str(out), "frames": c.frame_count})
    (work / "pairs.json").write_text(json.dumps(pairs))
    (work / "pairs.joints.json").write_text(json.dumps([j["name"] for j in side["joints"]]))
    lines = blender("blender_check.py", source, work / "pairs.json")
    print("\n".join("  " + x for x in lines))
    return any(x.startswith("BLENDER RESULT") for x in lines)


ok = True
work = work_root / "golem"
work.mkdir(parents=True, exist_ok=True)
make_golem.write(work / "golem.glb")
ok &= check("golem (make_golem.py)", work, work / "golem.glb",
            {"idle": ("Idle", True), "walk_f": ("WalkF", True), "fall": ("Fall", False), "slam": ("Slam", False), "flinch": ("Flinch", False)})
work = work_root / "authored"
work.mkdir(parents=True, exist_ok=True)
print("\n".join(blender("blender_author.py", work / "creature.glb")))
ok &= check("authored and exported by Blender", work, work / "creature.glb", {"idle": ("Idle", True), "walk": ("Walk", True), "grow": ("Grow", False)})
sys.exit(0 if ok else 1)
