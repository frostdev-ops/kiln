#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Draws cooked worn models on the player's body from your installed game, on the CPU, with the client's skinning and blend shape math: male and female, at rest and in a clip's pose, seen from
two sides. A look at where a piece sits and how it moves, not at what the client draws. Needs Pillow. Reads the game; writes one PNG, which holds the game's body: it stays on your machine.

    worn_render.py OUT.png MODEL.bin [MODEL.bin ...] [--hide Hair,Chest] [--clip GUID|largest] [--frame F] [--yaw DEG]
"""
import argparse
import math
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent), str(HERE.parent.parent / "kfc")]

import skin_render as R  # noqa: E402
import skindeform as D  # noqa: E402
from cook import steps  # noqa: E402
from kfc import anim, mesh  # noqa: E402
from kfc.archive import Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
BODY = (196, 170, 140)
PIECES = [(90, 130, 200), (190, 90, 70), (110, 180, 110)]


def body_models(k, t, female):
    th, dec = t.get("keen::ItemInfo")["hash"], Decoder(t)
    want = re.compile(rf"^VC_Body_(Head_{'Female' if female else 'Male'}_01_.*|(?!Head)\w+_01)$")
    out = []
    for key in k.resources_of_type(th):
        v = dec.decode(th, k.read(key))
        if want.match(v.get("debugName") or ""):
            eq = v["equipment"]
            model = eq.get("visualModel")
            if female:
                model = next((m["model"] for m in eq.get("visualModels") or [] if "Female" in m["mask"]), model)
            out.append((v["debugName"].split("_")[2], mesh.load_render_model(k, t, model)))
    return out


def box_seen(tris, yaw):
    """What fits the picture, as skin_render.shade turns the model."""
    c, s = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    xs = [c * p[0] + s * p[2] for t in tris for p in t[:3]]
    ys = [p[1] for t in tris for p in t[:3]]
    return ((max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2, max(max(xs) - min(xs), max(ys) - min(ys)) / 2 * 1.08)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out")
    ap.add_argument("models", nargs="+")
    ap.add_argument("--hide", default="", help="comma-separated body parts to leave out, as the look's hideMask would: Hair,Chest")
    ap.add_argument("--clip", default="largest")
    ap.add_argument("--frame", type=float, default=0.35)
    ap.add_argument("--yaw", type=float, default=25.0)
    ap.add_argument("--game", default=str(GAME))
    a = ap.parse_args(argv)
    game = Path(a.game)
    k, t = Kfc(game), TypeDb.from_exe(game / "enshrouded.exe")
    hier = mesh.load_hierarchy(k, t, steps.PLAYER_SKELETON)
    hide = {x.strip() for x in a.hide.split(",") if x.strip()}
    pieces = [(Decoder(t).decode("keen::RenderModel", Path(m).read_bytes()), Path(m).with_suffix(".blob").read_bytes()) for m in a.models]
    ja = t.get("keen::JointAnimation")
    guid = a.clip
    if guid == "largest":
        want = anim.hierarchy_hash(hier["jointNames"])
        guid = max((k.size_of(x), x.guid) for x in k.resources_of_type(ja["hash"]) if int.from_bytes(k.read(x)[:8], "little") == want)[1]
    clip = anim.parse(k.read(ResourceKey(guid, ja["hash"], 0)))
    dp = anim.parse_default_pose(k.read(ResourceKey(steps.PLAYER_SKELETON, t.get("keen::ModelHierarchyResource")["hash"], 0)))
    posed = D.pose_of_clip(hier, anim.sample_frame(clip, a.frame * clip.frame_count, dp))
    rest = D.rest_pose(hier)
    palette = [BODY] + PIECES
    panels = []
    for female in (False, True):
        body = [b for b in body_models(k, t, female) if b[0] not in hide]
        shapes = {"Female": 1.0} if female else {}
        for title, world, yaw in (("at rest", rest, a.yaw), ("behind", rest, a.yaw + 180), (f"clip {guid[:8]} at {a.frame:.2f}", posed, a.yaw)):
            tris = []
            for _, b in body:
                tris += [(x, y, z, 0) for x, y, z, _i in R.triangles_of(b.desc, b.data, hier, world, 0, shapes)]
            for i, (d, blob) in enumerate(pieces):
                tris += [(x, y, z, 1 + i) for x, y, z, _i in R.triangles_of(d, blob, hier, world, 0, shapes)]
            panels.append((f"{'female' if female else 'male'}, {title}", R.shade(tris, palette, yaw, box_seen(tris, yaw))))
    R.compose(panels).save(a.out)
    print(a.out, "panels", len(panels))
    return 0


if __name__ == "__main__":
    sys.exit(main())
