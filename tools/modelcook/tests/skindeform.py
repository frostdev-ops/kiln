# SPDX-License-Identifier: MIT
"""Linear blend skinning two ways, for checks: as glTF defines it from a source file, and as the game's client does it from a cooked model.

- `gltf_posed` reads a glTF and evaluates its skins per the glTF 2.0 specification, with its own matrix code, so it shares nothing with the cooker.
- `cooked_posed` takes a decoded RenderModel and its blob: per vertex, the sum of weight x joint pose x inverse bind x vertex. Joint poses come from a pose of local transforms chained
  through the skeleton's parents, and the inverse binds from the skeleton's skinToJointMatrices, as the client computes them (docs/re/models.md 5).

No third-party modules.
"""
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE.parent), str(HERE.parent.parent / "kfc"), str(HERE.parent.parent / "animcook")]

from anicook.gltfdoc import Doc  # noqa: E402
from cook.gltf import _accessor  # noqa: E402
from kfc import mesh as kmesh  # noqa: E402


def mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def trs(t, q, s):
    x, y, z, w = q
    r = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
         [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
         [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
    return [[r[i][0] * s[0], r[i][1] * s[1], r[i][2] * s[2], t[i]] for i in range(3)] + [[0.0, 0.0, 0.0, 1.0]]


def apply(m, p):
    return tuple(m[i][0] * p[0] + m[i][1] * p[1] + m[i][2] * p[2] + m[i][3] for i in range(3))


def chain(parents, locals_):
    """Local matrices -> world matrices. A parent comes before its child; 0xFFFF marks a root."""
    out = []
    for i, m in enumerate(locals_):
        out.append(m if parents[i] == 0xFFFF else mul(out[parents[i]], m))
    return out


def _matrix43(s):
    rot, pos = s["rot"], s["pos"]
    return [[rot[c][a] for c in "xyz"] + [pos[a]] for a in "xyz"] + [[0.0, 0.0, 0.0, 1.0]]


def pose_of_clip(hierarchy, clip_frame):
    """(rotations, positions, scales) of a sampled keen::JointAnimation frame -> world matrix per skeleton joint, game space."""
    rot, pos, scl = clip_frame
    return chain(hierarchy["jointParentIndices"], [trs(pos[j], rot[j], scl[j]) for j in range(hierarchy["jointCount"])])


def rest_pose(hierarchy):
    """World matrix per joint at the skeleton's default pose, game space."""
    ls = []
    for j in hierarchy["defaultPoseJoints"][:hierarchy["jointCount"]]:
        p, q, s = j["position"], j["orientation"], j["scale"]
        ls.append(trs((p["x"], p["y"], p["z"]), (q["x"], q["y"], q["z"], q["w"]), (s["x"], s["y"], s["z"])))
    return chain(hierarchy["jointParentIndices"], ls)


def cooked_posed(desc, blob, hierarchy, world, lod=0):
    """A decoded RenderModel posed by `world` (a matrix per skeleton joint). -> [positions per mesh of the LOD], game space."""
    m = kmesh.RenderModel(guid="", desc=desc, data=blob)
    skin = [mul(world[j], _matrix43(hierarchy["skinToJointMatrices"][j])) for j in desc["skinningMap"]]
    lo = desc["lods"][lod]
    out = []
    for ms in desc["meshes"][lo["meshOffset"]:lo["meshOffset"] + lo["meshCount"]]:
        pos = kmesh.decode_group(m, ms)["positions"]
        st = desc["skinningData"]["start"] + 8 * ms["skinVertexOffset"]
        res = []
        for v, p in enumerate(pos):
            b = blob[st + 8 * v:st + 8 * v + 8]
            acc = [0.0, 0.0, 0.0]
            for s in range(4):
                if b[4 + s]:
                    q = apply(skin[b[s]], p)
                    for k in range(3):
                        acc[k] += q[k] * b[4 + s] / 255.0
            res.append(tuple(acc))
        out.append(res)
    return out


def _gltf_world(doc, overrides):
    n = len(doc.nodes)
    out = [None] * n

    def world(i):
        if out[i] is None:
            t, q, s = doc.rest(i)
            if i in overrides:
                t, q, s = overrides[i].get("translation", t), overrides[i].get("rotation", q), overrides[i].get("scale", s)
            m = trs(t, q, s)
            p = doc.parent[i]
            out[i] = mul(world(p), m) if p is not None else m
        return out[i]

    for i in range(n):
        world(i)
    return out


def gltf_posed(path, animation=None, time=0.0):
    """Evaluates a glTF per its specification at `time` of `animation` (None = the nodes' rest transforms). -> [positions per skinned primitive, or per rigid primitive in world space],
    glTF space, in the order of a depth-first walk of the default scene."""
    doc = Doc(path)
    d, buffers = doc.doc, doc.buffers
    over = {}
    if animation:
        for node, chans in doc.animations()[animation].items():
            over[node] = {path_: ch.eval(time) for path_, ch in chans.items()}
    world = _gltf_world(doc, over)
    skins = d.get("skins", [])
    joint_m = []
    for sk in skins:
        ibm = _accessor(d, buffers, sk["inverseBindMatrices"]) if "inverseBindMatrices" in sk else None
        ms = []
        for i, j in enumerate(sk["joints"]):
            inv = [[ibm[i][c * 4 + r] for c in range(4)] for r in range(4)] if ibm else [[float(a == b) for b in range(4)] for a in range(4)]
            ms.append(mul(world[j], inv))
        joint_m.append(ms)
    out = []

    def walk(i):
        node = doc.nodes[i]
        if "mesh" in node:
            for p in d["meshes"][node["mesh"]]["primitives"]:
                at = p["attributes"]
                P = _accessor(d, buffers, at["POSITION"])
                if "skin" in node:
                    J, W = _accessor(d, buffers, at["JOINTS_0"]), _accessor(d, buffers, at["WEIGHTS_0"])
                    ms = joint_m[node["skin"]]
                    res = []
                    for v, q in enumerate(P):
                        tot = sum(W[v])
                        acc = [0.0, 0.0, 0.0]
                        for s in range(4):
                            if W[v][s] > 0:
                                r = apply(ms[J[v][s]], q)
                                for k in range(3):
                                    acc[k] += r[k] * W[v][s] / tot
                        res.append(tuple(acc))
                    out.append(res)
                else:
                    out.append([apply(world[i], q) for q in P])
        for c in node.get("children", []):
            walk(c)

    scene = d["scenes"][d.get("scene", 0)]
    for r in scene["nodes"]:
        walk(r)
    return out


def mirrored(prims):
    return [[(-p[0], p[1], p[2]) for p in ps] for ps in prims]


def max_distance(a, b):
    return max(sum((x[k] - y[k]) ** 2 for k in range(3)) ** 0.5 for pa, pb in zip(a, b) for x, y in zip(pa, pb))
