#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Draws cooked or game skinned models in a pose, on the CPU, with the client's skinning math (skindeform.py): a look at what the numbers mean. Needs Pillow. Reads the game only with
--game-model; writes one PNG.

    skin_render.py OUT.png --panel "title" MODEL.bin SKELETON.bin [CLIP.bin FRAME] [--panel ...]    cooked files
    skin_render.py OUT.png --game-model GUID [--clip GUID|largest] [--frame F] --with MODEL.bin      a game model, posed, next to a cooked one on the same skeleton
"""
import argparse
import math
import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent), str(HERE.parent.parent / "kfc")]

from PIL import Image, ImageDraw  # noqa: E402

import skindeform as D  # noqa: E402
from kfc import anim, mesh  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

TYPES = HERE.parent.parent / "animcook" / "types" / "anim-types.json"
MODEL_TYPES = HERE / "fixtures" / "types-rendermodel.json"
SIZE = 520
COLORS = [(196, 170, 140), (120, 150, 190), (170, 120, 110), (130, 170, 120), (180, 160, 90)]


def shade(tris, palette, yaw_deg, box):
    """tris: [(a, b, c, colour index)] in game space. Orthographic, looking at the +z side of the model, so its +x (its right) is on the left of the picture."""
    c, s = math.cos(math.radians(yaw_deg)), math.sin(math.radians(yaw_deg))
    img = Image.new("RGB", (SIZE, SIZE), (24, 28, 34))
    dr = ImageDraw.Draw(img)
    (cx, cy, half) = box
    scale = (SIZE / 2 - 12) / half

    def proj(p):
        x, z = c * p[0] + s * p[2], -s * p[0] + c * p[2]
        return (SIZE / 2 - (x - cx) * scale, SIZE / 2 - (p[1] - cy) * scale, z)

    items = []
    for a, b, d, col in tris:
        pa, pb, pd = proj(a), proj(b), proj(d)
        ux, uy, uz = pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2]
        vx, vy, vz = pd[0] - pa[0], pd[1] - pa[1], pd[2] - pa[2]
        nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
        ln = math.sqrt(nx * nx + ny * ny + nz * nz) or 1
        light = abs(nx * 0.3 - ny * 0.5 + nz * 0.8) / ln
        items.append((pa[2] + pb[2] + pd[2], pa, pb, pd, col, 0.25 + 0.75 * light))
    items.sort(key=lambda x: x[0])  # far first: the viewer is at +z
    for _, pa, pb, pd, col, li in items:
        rgb = tuple(int(x * li) for x in palette[col % len(palette)])
        dr.polygon([pa[:2], pb[:2], pd[:2]], fill=rgb)
    return img


def triangles_of(desc, blob, hier, world, lod=0):
    posed = D.cooked_posed(desc, blob, hier, world, lod)
    m = mesh.RenderModel(guid="", desc=desc, data=blob)
    lo = desc["lods"][lod]
    out = []
    for i, (ms, pos) in enumerate(zip(desc["meshes"][lo["meshOffset"]:lo["meshOffset"] + lo["meshCount"]], posed)):
        idx = mesh.indices_of(m, ms)
        for t in range(0, len(idx), 3):
            out.append((pos[idx[t]], pos[idx[t + 1]], pos[idx[t + 2]], i))
    return out


def box_of(tris):
    xs = [p[0] for t in tris for p in t[:3]]
    ys = [p[1] for t in tris for p in t[:3]]
    half = max(max(xs) - min(xs), max(ys) - min(ys)) / 2 * 1.08
    return ((max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2, half)


def compose(panels):
    w = sum(p[1].width for p in panels)
    out = Image.new("RGB", (w, SIZE + 22), (10, 12, 16))
    dr = ImageDraw.Draw(out)
    x = 0
    for title, im in panels:
        out.paste(im, (x, 22))
        dr.text((x + 6, 5), title, fill=(220, 220, 220))
        x += im.width
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out")
    ap.add_argument("--panel", action="append", nargs="+", default=[], help="title MODEL.bin SKELETON.bin [CLIP.bin FRAME]")
    ap.add_argument("--game-model")
    ap.add_argument("--clip", default="largest")
    ap.add_argument("--frame", type=float, default=0.4, help="a fraction of the clip's length")
    ap.add_argument("--with", dest="other", help="a cooked MODEL.bin on the game model's skeleton, drawn next to it")
    ap.add_argument("--with-title", default="cooked")
    ap.add_argument("--yaw", type=float, default=20.0)
    ap.add_argument("--game", default=str(Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"))
    a = ap.parse_args(argv)
    types = TypeDb.load(TYPES)
    mt = TypeDb.load(MODEL_TYPES)
    panels = []
    if a.game_model:
        from kfc.archive import Kfc, ResourceKey
        game = Path(a.game)
        k, t = Kfc(game), TypeDb.from_exe(game / "enshrouded.exe")
        m = mesh.load_render_model(k, t, a.game_model)
        hier = mesh.load_hierarchy(k, t, m.desc["hierarchy"])
        ht = t.get("keen::ModelHierarchyResource")
        dp = anim.parse_default_pose(k.read(ResourceKey(m.desc["hierarchy"], ht["hash"], 0)))
        ja = t.get("keen::JointAnimation")
        guid = a.clip
        if guid == "largest":
            want = anim.hierarchy_hash(hier["jointNames"])
            guid = max((k.size_of(x), x.guid) for x in k.resources_of_type(ja["hash"]) if int.from_bytes(k.read(x)[:8], "little") == want)[1]
        clip = anim.parse(k.read(ResourceKey(guid, ja["hash"], 0)))
        world = D.pose_of_clip(hier, anim.sample_frame(clip, min(a.frame * clip.frame_count, clip.frame_count - 1 + 0.999), dp))
        tris = triangles_of(m.desc, m.data, hier, world)
        box = box_of(tris)
        panels.append((f"game: {m.desc['debugName']}, clip {guid[:8]}, {len(tris)} tris", shade(tris, COLORS, a.yaw, box)))
        if a.other:
            c = Decoder(mt).decode("keen::RenderModel", Path(a.other).read_bytes())
            cb = Path(a.other).with_suffix(".blob").read_bytes()
            ct = triangles_of(c, cb, hier, world)
            panels.append((f"{a.with_title}: {len(ct)} tris", shade(ct, COLORS, a.yaw, box)))
    for spec in a.panel:
        title, model, skel = spec[:3]
        d = Decoder(mt).decode("keen::RenderModel", Path(model).read_bytes())
        blob = Path(model).with_suffix(".blob").read_bytes()
        hraw = Path(skel).read_bytes()
        hier = Decoder(types, max_array=1 << 20).decode(types.get("keen::ModelHierarchyResource"), hraw)
        world = D.rest_pose(hier)
        if len(spec) > 3:
            clip = anim.parse(Path(spec[3]).read_bytes())
            f = float(spec[4]) * clip.frame_count
            world = D.pose_of_clip(hier, anim.sample_frame(clip, min(f, clip.frame_count - 1 + 0.999), anim.parse_default_pose(hraw)))
        tris = triangles_of(d, blob, hier, world)
        panels.append((title, shade(tris, COLORS, a.yaw, (0.0, 1.0, 1.25))))
    compose(panels).save(a.out)
    print(a.out, Image.open(a.out).size)
    return 0


if __name__ == "__main__":
    sys.exit(main())
