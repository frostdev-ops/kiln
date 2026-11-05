# SPDX-License-Identifier: MIT
"""Skin data of a keen::RenderModel: binds a glTF's skinned primitives to a skeleton and writes the stream the game's vertex skinning reads.

The format (docs/re/models.md 5): 8 bytes per vertex, 4 x u8 indices into `skinningMap`, then 4 x u8 weights in descending order that sum to 255, unused
slots 0. `skinningMap` holds a u16 skeleton joint per model joint, in ascending order, and lists the joints that carry weight. Vertices are stored in bind pose:
the client multiplies each by the joint's chained pose and the skeleton's `skinToJointMatrices` entry, so a joint's bind matrix is the inverse of that entry.
"""
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent.parent / "kfc"))
sys.path.insert(0, str(HERE.parent.parent / "animcook"))

from kfc import mesh as kmesh  # noqa: E402

from . import gltf  # noqa: E402

MAX_INFLUENCES = 4
WEIGHT_SUM = 255
MAX_MODEL_JOINTS = 256  # the stream's indices are u8
MAX_LODS = 8  # the most any of the game's 10,470 models has; nothing in the loader limits it
MAX_MATERIALS = 12  # the same; the loader keeps a growing array
BIND_TOLERANCE = 2e-3  # of a matrix element: metres for a position, about that many radians for a rotation
DROPPED_WEIGHT_ERROR = 0.10  # a vertex that loses more than this to the 4-influence limit would visibly change shape
DROPPED_WEIGHT_WARN = 0.01


class SkinError(Exception):
    pass


def _identity():
    return [[1.0, 0.0, 0.0, 0.0], [0.0, 1.0, 0.0, 0.0], [0.0, 0.0, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0]]


def _mirror(m):
    """X M X with X the mirror on x: a transform between glTF space and the game's."""
    sign = (-1.0, 1.0, 1.0, 1.0)
    return [[m[r][c] * sign[r] * sign[c] for c in range(4)] for r in range(4)]


def _diff(a, b):
    return max(abs(a[r][c] - b[r][c]) for r in range(4) for c in range(4))


def _trs(t, q, s):
    r = kmesh._quat_mat(q)
    return [[r[i][j] * s[j] for j in range(3)] + [t[i]] for i in range(3)] + [[0.0, 0.0, 0.0, 1.0]]


def _matrix43(s):
    """A decoded keen::Matrix43: the columns x, y, z, then the translation."""
    rot, pos = s["rot"], s["pos"]
    return [[rot[c][a] for c in "xyz"] + [pos[a]] for a in "xyz"] + [[0.0, 0.0, 0.0, 1.0]]


class Target:
    """The skeleton a model is bound to. `bind[j]` is joint j's bind matrix in game space."""

    def __init__(self, guid, names, bind, source):
        self.guid, self.names, self.bind, self.source = guid, list(names), bind, source
        self.index = {n: i for i, n in enumerate(self.names)}

    def __len__(self):
        return len(self.names)

    def joint_of(self, doc, node):
        """The skeleton joint a glTF node stands for: by name, or by the joint index tools/kfc's export keeps in the node's extras."""
        j = self.index.get(doc["node_names"][node])
        if j is None:
            j = doc["node_extras"][node].get("keen_joint")
            if not (isinstance(j, int) and 0 <= j < len(self.names)):
                j = None
        return j

    @classmethod
    def from_skeleton(cls, sk, guid):
        """A skeleton of tools/animcook: its compiled form's inverse binds are the inverse of the chained rest pose."""
        chain = []
        for i, (t, q, s) in enumerate(sk.rest):
            local = _trs(t, q, s)
            chain.append(local if sk.parents[i] == 0xFFFF else kmesh._mul4(chain[sk.parents[i]], local))
        return cls(guid, sk.names, chain, sk.source)

    @classmethod
    def from_game(cls, value, guid):
        """A decoded keen::ModelHierarchyResource. Joints are named j<index>, as tools/kfc exports them: the game's own names are stripped."""
        n = value["jointCount"]
        return cls(guid, [f"j{i}" for i in range(n)], [kmesh._inv_affine(_matrix43(s)) for s in value["skinToJointMatrices"][:n]], "game")


def reduce_influences(pairs):
    """[(joint, weight)] -> (the at most 4 heaviest, summing to 1, heaviest first, the input's order among equals; the share of weight lost), None when nothing weighs."""
    merged = {}
    for j, w in pairs:
        merged[j] = merged.get(j, 0.0) + w
    total = sum(merged.values())
    if total <= 0:
        return None, 0.0
    items = sorted(merged.items(), key=lambda x: -x[1])
    kept = items[:MAX_INFLUENCES]
    kept_total = sum(w for _, w in kept)
    return [(j, w / kept_total) for j, w in kept], 1.0 - kept_total / total


def quantize(pairs):
    """[(joint, weight summing to 1)] -> [(joint, u8 weight summing to 255)], largest remainders round up, heaviest first, no zero weights."""
    scaled = [w * WEIGHT_SUM for _, w in pairs]
    out = [int(x) for x in scaled]
    for i in sorted(range(len(pairs)), key=lambda i: -(scaled[i] - out[i]))[:WEIGHT_SUM - sum(out)]:
        out[i] += 1
    return sorted(((pairs[i][0], out[i]) for i in range(len(pairs)) if out[i]), key=lambda x: -x[1])


def joint_map(lods):
    """`skinningMap`: the skeleton joints that carry weight anywhere in the model, ascending."""
    used = {j for lod in lods for p in lod for inf in p["influences"] for j, _ in inf}
    return sorted(used)


def encode_stream(influences, model_index):
    """One group's stream from [[(skeleton joint, u8 weight)], ...]. `model_index` maps a skeleton joint to its entry in `skinningMap`."""
    out = bytearray(8 * len(influences))
    for v, inf in enumerate(influences):
        for s, (j, w) in enumerate(inf):
            out[8 * v + s] = model_index[j]
            out[8 * v + 4 + s] = w
    return bytes(out)


def decode_stream(data):
    """The stream back to [[(model joint, u8 weight)], ...], weights above 0 only."""
    return [[(data[v + s], data[v + 4 + s]) for s in range(MAX_INFLUENCES) if data[v + 4 + s]] for v in range(0, len(data), 8)]


def _used_slots(prim):
    return sorted({s for inf in prim["influences"] for s, _ in inf})


def _bind_matrix(doc, prim, where):
    """The one transform that takes this primitive's mesh space to bind pose. glTF's rule is pose x inverse bind per joint; for a mesh bound in its rest pose those
    agree on every joint. When they do not, the rest pose is not the pose the mesh was bound in, and a stored bind pose can't reproduce that blend."""
    skin = doc["skins"][prim["skin"]]
    slots = _used_slots(prim)
    if not slots:
        raise SkinError(f"{where}: no vertex has a weight")
    if slots[-1] >= len(skin["joints"]):
        raise SkinError(f"{where}: JOINTS_0 uses joint {slots[-1]}, the skin has {len(skin['joints'])}")
    ms = {}
    for s in slots:
        ibm = skin["ibm"][s] if skin["ibm"] else _identity()
        ms[s] = gltf._mat_mul(doc["world"][skin["joints"][s]], ibm)
    first = ms[slots[0]]
    worst = max(slots, key=lambda s: _diff(ms[s], first))
    d = _diff(ms[worst], first)
    if d > BIND_TOLERANCE:
        raise SkinError(f"{where}: joints {doc['node_names'][skin['joints'][slots[0]]]!r} and {doc['node_names'][skin['joints'][worst]]!r} don't agree on the bind pose, by {d:.3f}: "
                        "the armature isn't at its rest pose in this file. Apply the pose as rest pose, or export at rest")
    return first


def _nearest_joint(doc, prim, target):
    for node in prim.get("ancestors", []):
        j = target.joint_of(doc, node)
        if j is not None:
            return j, node
    return None, None


def bind(doc, target):
    """Turns a read glTF into bind-pose primitives with influences on skeleton joints, in place. A mesh without a skin that hangs under a joint follows that joint.
    Returns warnings. Raises SkinError for what would draw wrong."""
    warnings = []
    fname = doc["file"]
    rest = {}  # skeleton joint -> the node standing for it
    for pi, p in enumerate(doc["primitives"]):
        where = f"{fname}, primitive {pi}"
        if "skin" in p:
            skin = doc["skins"][p["skin"]]
            m = _bind_matrix(doc, p, where)
            if _diff(m, _identity()) > 1e-7:
                pos, nrm, tan, det = gltf.transform_attributes(m, p["positions"], p["normals"], p["tangents"])
                p["positions"], p["normals"], p["tangents"] = pos, nrm, tan
                if det < 0:
                    idx = p["indices"]
                    for t in range(0, len(idx) - 2, 3):
                        idx[t + 1], idx[t + 2] = idx[t + 2], idx[t + 1]
            elif p["normals"]:
                p["normals"] = [gltf._norm(n) for n in p["normals"]]
            joints = {}
            for s in _used_slots(p):
                node = skin["joints"][s]
                j = target.joint_of(doc, node)
                if j is None:
                    raise SkinError(f"{where}: joint {doc['node_names'][node]!r} has weights and is not in the skeleton ({target.source}, {len(target)} joints: "
                                    f"{', '.join(target.names[:6])}{' ...' if len(target) > 6 else ''})")
                joints[s] = j
                rest.setdefault(j, node)
            weights = [[(joints[s], w) for s, w in inf] for inf in p["influences"]]
        else:
            j, node = _nearest_joint(doc, p, target)
            if j is None:
                raise SkinError(f"{where}: this mesh has no skin and no joint above it, so nothing would move it. Weight it, or parent it to a bone")
            rest.setdefault(j, node)
            weights = [[(j, 1.0)]] * len(p["positions"])
        bad = None
        over, worst = 0, 0.0
        out = []
        for v, pairs in enumerate(weights):
            reduced, lost = reduce_influences(pairs)
            if reduced is None:
                bad = v
                break
            if lost > 0:
                over += 1
                worst = max(worst, lost)
            out.append(quantize(reduced))
        if bad is not None:
            raise SkinError(f"{where}: vertex {bad} has no weight, so it would collapse to the origin. Weight every vertex")
        if worst > DROPPED_WEIGHT_ERROR:
            raise SkinError(f"{where}: {over} vertices have more than {MAX_INFLUENCES} influences, and keeping the heaviest {MAX_INFLUENCES} loses up to {worst:.0%} of a vertex's weight. "
                            f"Limit them to {MAX_INFLUENCES} in your tool (in Blender: Weights > Limit Total)")
        if worst > DROPPED_WEIGHT_WARN:
            warnings.append(f"{where}: {over} vertices have more than {MAX_INFLUENCES} influences; keeping the heaviest {MAX_INFLUENCES} loses up to {worst:.1%} of a vertex's weight")
        p["influences"] = out
    _check_rest(doc, target, rest)
    return warnings


def _check_rest(doc, target, rest):
    """The file's rest pose has to be the skeleton's bind pose: the game poses the mesh from there, so a difference moves every vertex by it."""
    off = []
    for j, node in rest.items():
        d = _diff(_mirror(doc["world"][node]), target.bind[j])
        if d > BIND_TOLERANCE:
            off.append((d, j, node))
    if off:
        off.sort(reverse=True)
        names = ", ".join(f"{doc['node_names'][n]} ({d:.3f})" for d, _, n in off[:5])
        raise SkinError(f"{doc['file']}: {len(off)} joints are not where skeleton {target.guid} ({target.source}) has them. Largest difference first: {names}. "
                        "The mesh has to be bound to that skeleton's pose. On one of the game's skeletons, start from an export of it (tools/kfc: model GUID --gltf F --skin)")


def check_limits(lods, pixel_sizes, materials):
    """Model-level limits, for a clear error here instead of a model that fails to load or draws wrong."""
    if len(lods) > MAX_LODS:
        raise SkinError(f"{len(lods)} LODs: no game model has more than {MAX_LODS}")
    if pixel_sizes:
        if len(pixel_sizes) != len(lods):
            raise SkinError(f"{len(lods)} LODs and {len(pixel_sizes)} pixel sizes: give one per LOD, or none")
        if pixel_sizes[0] != 0:
            raise SkinError(f"LOD 0's pixel size is {pixel_sizes[0]}: it has to be 0, the level drawn at any size")
        if any(a >= b for a, b in zip(pixel_sizes, pixel_sizes[1:])):
            raise SkinError(f"pixel sizes {pixel_sizes}: each LOD's has to be larger than the one before")
    if not materials:
        raise SkinError("the model has no material: give \"material\" or \"materialMap\"")
    if len(materials) > MAX_MATERIALS:
        raise SkinError(f"{len(materials)} materials: no game model has more than {MAX_MATERIALS}. Merge them, or split the model")
