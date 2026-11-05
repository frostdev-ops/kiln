#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A manual check that a cooked clip plays in the game client (docs/kiln/modding/animations.md): override one game clip with our encoder's output, with one joint visibly changed.
Writes assets/ of mods/tests/clipcheck on your machine, from your installed game; those files are git-ignored.

    python3 tools/animcook/samples/clip_override.py [--check head|arm]
    python3 tools/animcook/samples/clip_override.py --clip GUID --joint N --degrees 30 [--axis z] [--ramp] --template NAME

head: the Vukah's Idle (d099cbe2-...), its head joint turned 30 degrees, held all through the clip.
arm: a Fogger's idle, an arm joint bending 0..60..0 degrees over the clip: the mesh must follow the bone.

Every frame of the clip is read from the game, the change applied, then encoded by anim.encode, parsed back, and compared with the intended pose: if the check fails nothing is
written. It prints the commands to deploy and what to look for.
"""
import argparse
import json
import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE.parent.parent / "kfc")]

from kfc import anim  # noqa: E402
from kfc.archive import Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
GRAPH = "keen::anim_graph::runtime_graph::AnimationGraphResource2_0"
TEMPLATE = "keen::ecs::TemplateResource"
SLOT_HEAD, SLOT_ARM_L = 1, 12


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return [aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx, aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz]


def axis_quat(axis, deg):
    h = math.radians(deg) / 2
    v = [0.0, 0.0, 0.0, math.cos(h)]
    v["xyz".index(axis)] = math.sin(h)
    return v


def idle_of(k, t, dec, template_name):
    """(template GUID, graph value, clip GUID of the state named Idle) of a game template."""
    tt = t.get(TEMPLATE)
    for key in k.resources_of_type(tt["hash"]):
        v = dec.decode(tt, k.read(key))
        if v["name"] != template_name:
            continue
        comps = {c["$type"].split("::")[-1]: c["value"] for c in v["components"] if c}
        gt = t.get(GRAPH)
        g = dec.decode(gt, k.read(ResourceKey(comps["AnimationGraphResourceComponent2"]["animationGraph"], gt["hash"], 0)))
        nodes = {n["value"]["id"]["value"]: n["value"] for n in g["nodeDefinitions"]}
        for n in nodes.values():
            if n.get("type") == "StateMachineState" and n.get("debugName", "").lower() == "idle":
                p = nodes.get(n["poseResult"]["value"])
                i = nodes.get(p["input"]["value"]) if p else None
                if i and i.get("type") == "SampleAnimationClip":
                    return key.guid, g, i["animation"]
        raise SystemExit(f"{template_name}: no plain Idle clip state")
    raise SystemExit(f"no template {template_name}")


def make(k, t, dec, clip_guid, joint, axis, degrees, ramp):
    ja = t.get("keen::JointAnimation")
    raw = k.read(ResourceKey(clip_guid, ja["hash"], 0))
    clip = anim.parse(raw)
    mh = t.get("keen::ModelHierarchyResource")
    hraw = next((r for r in (k.read(x) for x in k.resources_of_type(mh["hash"])) if anim.hierarchy_hash(dec.decode(mh, r)["jointNames"]) == clip.hierarchy_hash), None)
    if hraw is None:
        raise SystemExit("no skeleton has this clip's hierarchy hash")
    dp = anim.parse_default_pose(hraw)
    n = clip.frame_count
    frames = []
    for f in range(n + 1):
        rot, pos, scl = anim.sample_frame(clip, min(float(f), n - 1 + 0.999), dp)
        u = f / n
        d = degrees * (math.sin(math.pi * u) if ramp else 1.0)
        rot = [list(q) for q in rot]
        rot[joint] = qmul(rot[joint], axis_quat(axis, d))
        frames.append((rot, [list(p) for p in pos], [list(s) for s in scl]))
    out = anim.build(anim.encode(frames, dp, clip.hierarchy_hash, fps=clip.fps, loop=clip.flags == 1))
    back = anim.parse(out)
    if anim.build(back) != out:
        raise SystemExit("the new clip does not rebuild byte-identical")
    worst = 0.0
    for f in range(0, n + 1, max(1, n // 200)):
        rot = anim.sample_frame(back, min(float(f), n - 1 + 0.999), dp)[0]
        for j in range(clip.joint_count):
            a, b = rot[j], frames[f][0][j]
            na = math.sqrt(sum(x * x for x in a)) or 1.0
            nb = math.sqrt(sum(x * x for x in b)) or 1.0
            worst = max(worst, 2 * math.degrees(math.acos(min(1.0, abs(sum(x * y for x, y in zip(a, b))) / (na * nb)))))
    if worst > 3.0:
        raise SystemExit(f"the encoded clip is {worst:.2f} degrees from the intended pose")
    return raw, out, worst, clip


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=str(HERE.parent.parent.parent / "mods/tests/clipcheck"), help="the mod folder whose assets/ to write")
    ap.add_argument("--check", choices=("head", "arm"), default="head")
    ap.add_argument("--clip")
    ap.add_argument("--joint", type=int)
    ap.add_argument("--axis", default="z", choices=list("xyz"))
    ap.add_argument("--degrees", type=float)
    ap.add_argument("--ramp", action="store_true", help="0..degrees..0 over the clip instead of held")
    ap.add_argument("--template", help="with --clip: the creature that plays it, for `clipcheck spawn`")
    ap.add_argument("--game", default=str(GAME))
    a = ap.parse_args(argv)
    game = Path(a.game).expanduser()
    k, t = Kfc(game), TypeDb.from_exe(game / "enshrouded.exe")
    dec = Decoder(t, max_array=1 << 26)
    if a.clip:
        if a.joint is None or a.degrees is None:
            ap.error("--clip needs --joint and --degrees")
        clip_guid, joint, axis, degrees, ramp, who, look = a.clip, a.joint, a.axis, a.degrees, a.ramp, a.template or "Enemy_Vukah_Melee", "the changed joint"
    elif a.check == "head":
        _, g, clip_guid = idle_of(k, t, dec, "Enemy_Vukah_Melee")
        joint, axis, degrees, ramp = g["slotBoneIndexMapping"][SLOT_HEAD], "z", 30.0, False
        who, look = "Enemy_Vukah_Melee", "the head is tilted about 30 degrees all through its idle, and moves smoothly, without a jump about once a second (63-frame segment joins)"
    else:
        _, g, clip_guid = idle_of(k, t, dec, "Enemy_Fogger_Soldier01")
        joint, axis, degrees, ramp = g["slotBoneIndexMapping"][SLOT_ARM_L] or g["slotBoneIndexMapping"][9], "z", 60.0, True
        who, look = "Enemy_Fogger_Soldier01", "one arm slowly bends away and back over its idle; the mesh follows the bone, with no spikes"
    raw, out, worst, clip = make(k, t, dec, clip_guid, joint, axis, degrees, ramp)
    root = Path(a.out)
    (root / "assets/cooked").mkdir(parents=True, exist_ok=True)
    (root / "assets/cooked/clip.bin").write_bytes(out)
    (root / "assets/assets.json").write_text(json.dumps({"format": 1, "entries": [{"kind": "resource", "guid": clip_guid, "type": "keen::JointAnimation", "part": 0,
                                                                                   "file": "cooked/clip.bin", "mode": "override"}]}, indent=2) + "\n")
    (root / "assets/check.json").write_text(json.dumps({"template": who, "clip": clip_guid, "joint": joint, "axis": axis, "degrees": degrees, "ramp": ramp}, indent=2) + "\n")
    print(f"{who}, clip {clip_guid}: {clip.frame_count} frames, joint {joint}, axis {axis}, {degrees:g} degrees{' (0..max..0)' if ramp else ''}")
    print(f"game clip {len(raw)} B, ours {len(out)} B, largest error against the intended pose {worst:.2f} degrees")
    print(f"written to {root / 'assets'}")
    print()
    print("Next, in this order (docs/kiln/modding/animations.md, Manual checks):")
    print(f"  dotnet build {root / 'ClipCheck.csproj'} -c Release -o /tmp/clipcheck")
    print("  tools/client/client.sh backup-saves")
    print("  tools/client/client.sh deploy --real --policy any --mod /tmp/clipcheck")
    print("  start the game, play a local world")
    print(f"  echo 'clipcheck spawn' > {game / 'kiln/control/console/c.cmd'}")
    print("  look for:", look)
    print("  quit the game, then: tools/client/client.sh undeploy --real")
    return 0


if __name__ == "__main__":
    sys.exit(main())
