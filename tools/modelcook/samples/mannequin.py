#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes a mesh of our own for one of the game's skeletons: a stick figure of tubes along its bones, with smooth weights, so a skeleton's clips can be watched on a mesh this tool made. The
output holds the skeleton's rest pose, read from your installed game: it stays on your machine.

    python3 tools/modelcook/samples/mannequin.py OUT.gltf --model GUID [--game DIR]

`--model` names one of the game's models on that skeleton: the mesh uses the joints that model uses, which leaves out helper joints nothing is weighted to.
"""
import argparse
import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent.parent / "kfc")]

from rigged_figure import Mesh  # noqa: E402

from kfc import mesh  # noqa: E402
from kfc.archive import Kfc  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
SHORTEST_BONE = 0.03  # fingers and face joints would only clutter the figure


def build(skeleton, used):
    """skeleton: mesh.skeleton_rh(...); used: joints to draw between. -> a Mesh with weights on those joint indices."""
    used = set(used)

    def used_parent(i):
        p = skeleton[i]["parent"]
        while p is not None and p not in used:
            p = skeleton[p]["parent"]
        return p

    def at(i):
        g = skeleton[i]["global"]
        return (g[0][3], g[1][3], g[2][3])

    m = Mesh()
    for j in sorted(used):
        p = used_parent(j)
        if p is None:
            continue
        a, b = at(p), at(j)
        length = math.dist(a, b)
        if length < SHORTEST_BONE:
            continue
        r = min(0.09, 0.012 + length * 0.12)

        def weights(u, p=p, j=j):
            if u < 0.75:
                return [(p, 1.0)]
            s = 0.5 * (u - 0.75) / 0.25
            return [(p, 1 - s), (j, s)]

        m.tube(a, b, r, r * 0.8, weights, 6, 4)
    return m


def to_tris(m):
    tri = mesh.Tris()
    joints, weights = [], []
    for inf in m.W:
        slots = (inf + [(0, 0.0)] * 4)[:4]
        joints.append(tuple(j for j, _ in slots))
        weights.append(tuple(w for _, w in slots))
    tri.submeshes.append({"material": "body", "materialIndex": 0, "positions": m.P, "normals": m.N, "uvs": m.UV, "indices": m.I, "joints": joints, "weights": weights})
    return tri


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out")
    ap.add_argument("--model", required=True)
    ap.add_argument("--game", default=str(GAME))
    a = ap.parse_args(argv)
    game = Path(a.game).expanduser()
    k, t = Kfc(game), TypeDb.from_exe(game / "enshrouded.exe")
    model = mesh.load_render_model(k, t, a.model)
    sk = mesh.skeleton_rh(mesh.load_hierarchy(k, t, model.desc["hierarchy"]))
    m = build(sk, model.desc["skinningMap"])
    Path(a.out).parent.mkdir(parents=True, exist_ok=True)
    mesh.write_gltf(to_tris(m), a.out, name="mannequin", skeleton=sk)
    print(f"{a.out}: {len(m.P)} vertices, {len(m.I) // 3} triangles on {len(sk)} joints of {model.desc['hierarchy']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
