#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A creature authored in Blender, exported by Blender's glTF exporter, cooked by tools/animcook and tools/modelcook and decoded again; each vertex posed as the game's client poses it
(skindeform.py) is compared with where Blender's own armature modifier puts it. Weights are stored in 255ths, so a vertex between joints that moved a metre apart can be off by 2 mm. Needs `blender` on PATH; no game.

    python3 tools/modelcook/tests/run_blender_check.py [WORK_DIR]
"""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path[:0] = [str(HERE), str(ROOT), str(ROOT.parent / "kfc"), str(ROOT.parent / "animcook")]

import skindeform as D  # noqa: E402
from anicook import recipe as AR  # noqa: E402
from cook import gltf, rendermodel as RM, skin as S  # noqa: E402
from kfc import anim  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

TYPES = TypeDb.load(ROOT / "tests" / "fixtures" / "types-rendermodel.json")
MATERIAL = {"material": "71165a08-1a37-495c-9cb9-39cd1145ebf0", "renderEffectId": "Pbr", "materialFlags": []}
ANIMATIONS = {"bend": "Bend", "wave": "Wave"}


def nearest_all(a, b):
    """For each point of a, the distance to the nearest of b."""
    return [min(sum((p[k] - q[k]) ** 2 for k in range(3)) for q in b) ** 0.5 for p in a]


def main():
    work = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(tempfile.mkdtemp())
    work.mkdir(parents=True, exist_ok=True)
    glb, ev = work / "creature.glb", work / "creature.eval.json"
    r = subprocess.run(["blender", "-b", "--factory-startup", "--python", str(HERE / "blender_skin_author.py"), "--", str(glb), str(ev)], capture_output=True, text=True, timeout=500)
    print("\n".join(x for x in r.stdout.splitlines() if x.startswith("BLENDER") or "Error" in x or "Traceback" in x))
    if not ev.exists():
        print(r.stdout[-2000:], r.stderr[-2000:])
        return 1
    evald = json.loads(ev.read_text())
    (work / "cook.json").write_text(json.dumps({"format": 1, "tool": "animcook", "mod": "check", "steps": [
        {"op": "skeleton", "name": "g", "gltf": "creature.glb"},
        {"op": "clips", "skeleton": "g", "gltf": "creature.glb", "clips": {k: {"animation": a} for k, a in ANIMATIONS.items()}}]}))
    atypes = TypeDb.load(ROOT.parent / "animcook" / "types" / "anim-types.json")
    rep = AR.Run(work / "cook.json", atypes).run(update_manifest=True)
    assert rep["ok"], rep
    hraw = (work / "cooked/g.skeleton.bin").read_bytes()
    hier = Decoder(atypes, max_array=1 << 20).decode(atypes.get(AR.HIER), hraw)
    dp = anim.parse_default_pose(hraw)
    side = json.loads((work / "cooked/g.skeleton.json").read_text())
    from anicook import skeleton as K
    target = S.Target.from_skeleton(K.load_sidecar(work / "cooked/g.skeleton.json"), side["guid"])
    doc = gltf.read(glb)
    print("glTF:", len(doc["primitives"]), "primitives,", sum(len(p["positions"]) for p in doc["primitives"]), "vertices; largest influence count", max(len(i) for p in doc["primitives"] for i in p.get("influences", [])))
    print("warnings:", S.bind(doc, target))
    lod = []
    for prim in doc["primitives"]:
        p = RM.prepare(prim)
        p["material_slot"] = 0
        lod.append(p)
    desc, blob = RM.build([lod], [MATERIAL], {"debugName": "", "materials": [], "meshlets": [], "meshes": [], "lods": []}, "creature",
                          skin={"hierarchy": target.guid, "hierarchyJointCount": len(target), "globalScale": 1.0})
    data = RM.encode(TYPES, desc)
    d = Decoder(TYPES).decode("keen::RenderModel", data)
    print("cooked:", d["jointCount"], "of", d["hierarchyJointCount"], "joints, blob", len(blob), "B")
    worst = 0.0
    moved = 0.0
    rest = None
    for key, name in ANIMATIONS.items():
        clip = anim.parse((work / f"cooked/clip_{key}.bin").read_bytes())
        for pose, pts in evald["poses"].items():
            aname, frame = pose.split(":")
            if aname != name:
                continue
            frame = int(frame)
            world = D.pose_of_clip(hier, anim.sample_frame(clip, min(float(frame), clip.frame_count - 1 + 0.999), dp))
            got = [p for ps in D.mirrored(D.cooked_posed(d, blob, hier, world)) for p in ps]
            dist = nearest_all(got, pts)
            back = nearest_all(pts, got)
            w = max(max(dist), max(back))
            worst = max(worst, w)
            if rest is None:
                rest = pts
            moved = max(moved, max(nearest_all(pts, rest)))
            print(f"  {pose}: largest distance cooked to Blender {w * 1000:.3f} mm over {len(got)} vertices")
    print(f"RESULT largest {worst * 1000:.3f} mm; control: the poses differ from the first by up to {moved * 1000:.0f} mm")
    return 0 if worst < 0.005 and moved > 0.2 else 1


if __name__ == "__main__":
    sys.exit(main())
