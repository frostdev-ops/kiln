# SPDX-License-Identifier: MIT
"""A skeleton: what `op: skeleton` cooks, what clips and graphs are checked against, and what other cook steps read to bind a mesh to it
(`from_gltf`, `Skeleton.joint_of_node`, the `<out>/<name>.skeleton.json` sidecar; docs/kiln/modding/animations.md)."""
import json
from pathlib import Path

from cook.gltf import GltfError
from kfc import anim, animgraph

from .gltfdoc import Doc, compose, to_game

SYNTHETIC_ROOT = "kiln_root"
ROOT_PARENT = 0xFFFF


class SkeletonError(Exception):
    pass


class Skeleton:
    """names, parents (0xFFFF for the root), rest (t, q, s) per joint in game space, parent-relative. `nodes[i]` is joint i's glTF
    node index in the file it came from, None for a synthetic root or a game skeleton."""

    def __init__(self, names, parents, rest, nodes=None, guid=None, source=None):
        self.names, self.parents, self.rest = list(names), list(parents), list(rest)
        self.nodes = list(nodes) if nodes else [None] * len(self.names)
        self.guid, self.source = guid, source
        self.index = {n: i for i, n in enumerate(self.names)}

    def __len__(self):
        return len(self.names)

    def joint_of_node(self, node):
        """The joint a glTF node became, None when it is not one: how a mesh's `skins[i].joints` map onto this skeleton."""
        return self.nodes.index(node) if node in self.nodes else None

    def default_pose(self):
        """(quats, positions, scales), as anim.parse_default_pose returns them."""
        return [r[1] for r in self.rest], [r[0] for r in self.rest], [r[2] for r in self.rest]

    def value(self, guid):
        v, _ = animgraph.skeleton_value(guid, self.names, self.parents, [r[0] for r in self.rest], [r[1] for r in self.rest],
                                        [r[2] for r in self.rest])
        return v

    @property
    def name_hashes(self):
        return [animgraph.joint_name_hash(n) for n in self.names]

    @property
    def hierarchy_hash(self):
        return anim.hierarchy_hash(self.name_hashes)

    def to_json(self, guid):
        """The sidecar next to the cooked skeleton: a mod's other cook steps read this, not the binary."""
        return {"format": 1, "tool": "animcook", "guid": guid, "source": self.source, "hierarchyHash": f"0x{self.hierarchy_hash:016x}",
                "joints": [{"index": i, "name": n, "parent": None if p == ROOT_PARENT else p, "nameHash": f"0x{h:08x}",
                            "translation": list(r[0]), "rotation": list(r[1]), "scale": list(r[2])}
                           for i, (n, p, h, r) in enumerate(zip(self.names, self.parents, self.name_hashes, self.rest))]}

    @classmethod
    def from_json(cls, doc):
        return cls([j["name"] for j in doc["joints"]], [ROOT_PARENT if j["parent"] is None else j["parent"] for j in doc["joints"]],
                   [(tuple(j["translation"]), tuple(j["rotation"]), tuple(j["scale"])) for j in doc["joints"]],
                   guid=doc.get("guid"), source=doc.get("source"))

    def compare(self, other, tol=0.001):
        """What differs (joint names, parents, rest positions): empty when clips of one fit the other."""
        out = []
        if self.names != other.names:
            only_a = [n for n in self.names if n not in other.index]
            only_b = [n for n in other.names if n not in self.index]
            if only_a or only_b:
                if only_a:
                    out.append(f"only in the skeleton: {', '.join(only_a[:8])}")
                if only_b:
                    out.append(f"only in this file: {', '.join(only_b[:8])}")
                return out
        for n, i in self.index.items():
            j = other.index[n]
            pa = self.names[self.parents[i]] if self.parents[i] != ROOT_PARENT else None
            pb = other.names[other.parents[j]] if other.parents[j] != ROOT_PARENT else None
            if pa != pb:
                out.append(f"joint {n}: parent {pa} in the skeleton, {pb} in this file")
            d = max(abs(a - b) for a, b in zip(self.rest[i][0], other.rest[j][0]))
            if d > tol:
                out.append(f"joint {n}: rest position differs by {d:.4f} m")
        return out


def _skin_joints(doc, skin, root):
    d = doc.doc
    skins = d.get("skins", [])
    if root is not None:
        i = doc.by_name.get(root)
        if i is None:
            raise SkeletonError(f"{doc.path.name}: no node named {root!r}")
        out, stack = [], [i]
        while stack:
            n = stack.pop()
            out.append(n)
            stack.extend(c for c in reversed(doc.nodes[n].get("children", [])) if "mesh" not in doc.nodes[c])
        return out
    if skins:
        if skin is None and len(skins) > 1:
            sets = {tuple(sorted(s["joints"])) for s in skins}
            if len(sets) > 1:
                raise SkeletonError(f"{doc.path.name} has {len(skins)} skins with different joints: name one with \"skin\"")
            skin = 0
        return list(skins[skin or 0]["joints"])
    # no skin: the one tree of mesh-free nodes the animations target
    targets = {n for a in doc.animations().values() for n in a}
    if not targets:
        raise SkeletonError(f"{doc.path.name}: no skin and no animated node: name the root joint with \"root\"")
    roots = set()
    for n in targets:
        while doc.parent[n] is not None and "mesh" not in doc.nodes[doc.parent[n]]:
            n = doc.parent[n]
        roots.add(n)
    if len(roots) != 1:
        raise SkeletonError(f"{doc.path.name}: no skin and several animated trees ({', '.join(doc.names[r] for r in sorted(roots))}): "
                            "name the root joint with \"root\"")
    return _skin_joints(doc, None, doc.names[roots.pop()])


def from_gltf(path, skin=None, root=None):
    """The skeleton of a glTF file. Joint order: parents first, depth first from the root, children as in the file. A node above
    the root joint (Blender's Armature object) is baked into the root's rest pose. Several roots get one synthetic root."""
    doc = Doc(path) if not isinstance(path, Doc) else path
    joints = _skin_joints(doc, skin, root)
    if len(set(joints)) != len(joints):
        raise SkeletonError(f"{doc.path.name}: a joint is listed twice")
    jset = set(joints)
    names = [doc.names[j] for j in joints]
    if len(set(names)) != len(names):
        dup = sorted({n for n in names if names.count(n) > 1})
        raise SkeletonError(f"{doc.path.name}: joint names must be unique, {', '.join(dup[:6])} repeat: they are how clips find their joints")
    if SYNTHETIC_ROOT in names:
        raise SkeletonError(f"{SYNTHETIC_ROOT} is a reserved joint name")
    roots = [j for j in joints if doc.parent[j] not in jset]
    order = []

    def walk(j):
        order.append(j)
        for c in doc.nodes[j].get("children", []):
            if c in jset:
                walk(c)

    for r in sorted(roots, key=joints.index):
        walk(r)
    missing = [doc.names[j] for j in joints if j not in order]
    if missing:
        raise SkeletonError(f"{doc.path.name}: joints not under a root: {', '.join(missing[:6])}")
    if len(order) + (len(roots) > 1) > 255:
        raise SkeletonError(f"{doc.path.name}: {len(order)} joints, the game's indices hold 255")
    out_names, out_parents, out_rest, out_nodes = [], [], [], []
    if len(roots) > 1:
        out_names.append(SYNTHETIC_ROOT)
        out_parents.append(ROOT_PARENT)
        out_rest.append(((0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)))
        out_nodes.append(None)
    idx = {}
    for j in order:
        idx[j] = len(out_names)
        trs = doc.rest(j)
        if j in roots:
            above = doc.world_of_parent(j)
            if above is not None:
                trs = compose(above, trs)
            par = 0 if len(roots) > 1 else ROOT_PARENT
        else:
            par = idx[doc.parent[j]]
        out_names.append(doc.names[j])
        out_parents.append(par)
        out_rest.append(to_game(*trs))
        out_nodes.append(j)
    return Skeleton(out_names, out_parents, out_rest, out_nodes, source=doc.path.name)


def from_game(hierarchy_value, guid):
    """A game skeleton, joints named j<index> as tools/kfc exports them (their real names are stripped)."""
    n = hierarchy_value["jointCount"]
    rest = []
    for j in hierarchy_value["defaultPoseJoints"][:n]:
        p, q, s = j["position"], j["orientation"], j["scale"]
        rest.append(((p["x"], p["y"], p["z"]), (q["x"], q["y"], q["z"], q["w"]), (s["x"], s["y"], s["z"])))
    sk = Skeleton([f"j{i}" for i in range(n)], hierarchy_value["jointParentIndices"], rest, guid=guid, source="game")
    sk.game_hash = anim.hierarchy_hash(hierarchy_value["jointNames"])
    return sk


def load_sidecar(path):
    return Skeleton.from_json(json.loads(Path(path).read_text()))


__all__ = ["GltfError", "Skeleton", "SkeletonError", "from_game", "from_gltf", "load_sidecar"]
