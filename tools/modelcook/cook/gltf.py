# SPDX-License-Identifier: MIT
"""Minimal glTF 2.0 reader (stdlib only): .gltf (embedded data URIs or external .bin) and .glb.

Returns the triangles of every mesh primitive reachable from the default scene, with node transforms applied:
positions, normals, tangents, uv0, colour0, indices and morph targets' position deltas. A primitive of a skinned node keeps
its mesh-space attributes, as glTF ignores that node's transform, and adds its influences; cook/skin.py binds it to a
skeleton. A mesh node named reference_* is left out: it is a body to model around, as `kiln skeleton player` writes.
"""
import base64
import json
import math
import struct
from pathlib import Path

COMP = {5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2), 5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
NORM_DIV = {5120: 127.0, 5121: 255.0, 5122: 32767.0, 5123: 65535.0}
REFERENCE = "reference_"


class GltfError(Exception):
    pass


def _load(path):
    path = Path(path)
    raw = path.read_bytes()
    bins = []
    if raw[:4] == b"glTF":
        _, _, length = struct.unpack_from("<III", raw, 0)
        o = 12
        doc = None
        while o < length:
            clen, ctype = struct.unpack_from("<II", raw, o)
            chunk = raw[o + 8:o + 8 + clen]
            if ctype == 0x4E4F534A:
                doc = json.loads(chunk.decode())
            elif ctype == 0x004E4942:
                bins.append(chunk)
            o += 8 + clen
        if doc is None:
            raise GltfError("glb without JSON chunk")
    else:
        doc = json.loads(raw.decode())
    buffers = []
    for i, b in enumerate(doc.get("buffers", [])):
        uri = b.get("uri")
        if uri is None:
            buffers.append(bins[0] if bins else b"")
        elif uri.startswith("data:"):
            buffers.append(base64.b64decode(uri.split(",", 1)[1]))
        else:
            buffers.append((path.parent / uri).read_bytes())
    return doc, buffers


def _elements(doc, buffers, view, offset, comp, n, count, norm):
    fmt, size = COMP[comp]
    bv = doc["bufferViews"][view]
    data = buffers[bv["buffer"]]
    base = bv.get("byteOffset", 0) + offset
    stride = bv.get("byteStride") or size * n
    out = []
    for i in range(count):
        vals = struct.unpack_from("<" + fmt * n, data, base + i * stride)
        if norm and comp in NORM_DIV:
            vals = tuple(max(v / NORM_DIV[comp], -1.0) for v in vals)
        out.append(vals)
    return out


def _accessor(doc, buffers, idx):
    a = doc["accessors"][idx]
    n = NCOMP[a["type"]]
    count = a["count"]
    norm = a.get("normalized", False)
    if "bufferView" in a:
        out = _elements(doc, buffers, a["bufferView"], a.get("byteOffset", 0), a["componentType"], n, count, norm)
    else:
        out = [tuple([0] * n) for _ in range(count)]
    if "sparse" in a:  # Blender writes shape keys this way
        sp = a["sparse"]
        idxs = _elements(doc, buffers, sp["indices"]["bufferView"], sp["indices"].get("byteOffset", 0), sp["indices"]["componentType"], 1, sp["count"], False)
        vals = _elements(doc, buffers, sp["values"]["bufferView"], sp["values"].get("byteOffset", 0), a["componentType"], n, sp["count"], norm)
        for i, v in zip(idxs, vals):
            out[i[0]] = v
    return out


def _mat_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def _node_matrix(node):
    if "matrix" in node:
        m = node["matrix"]  # column-major
        return [[m[c * 4 + r] for c in range(4)] for r in range(4)]
    t = node.get("translation", [0, 0, 0])
    q = node.get("rotation", [0, 0, 0, 1])
    s = node.get("scale", [1, 1, 1])
    x, y, z, w = q
    r = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
         [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
         [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
    return [[r[i][0] * s[0], r[i][1] * s[1], r[i][2] * s[2], t[i]] for i in range(3)] + [[0, 0, 0, 1]]


def _xf_point(m, p):
    return tuple(m[i][0] * p[0] + m[i][1] * p[1] + m[i][2] * p[2] + m[i][3] for i in range(3))


def _xf_dir(m, d):
    return tuple(m[i][0] * d[0] + m[i][1] * d[1] + m[i][2] * d[2] for i in range(3))


def _normal_matrix(m):
    a = [[m[i][j] for j in range(3)] for i in range(3)]
    det = (a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1]) - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0])
           + a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]))
    if abs(det) < 1e-20:
        raise GltfError("degenerate node transform")
    inv = [[(a[(j + 1) % 3][(i + 1) % 3] * a[(j + 2) % 3][(i + 2) % 3] - a[(j + 1) % 3][(i + 2) % 3] * a[(j + 2) % 3][(i + 1) % 3]) / det
            for j in range(3)] for i in range(3)]
    # transpose of the inverse
    return [[inv[j][i] for j in range(3)] + [0] for i in range(3)] + [[0, 0, 0, 1]], det


def _norm(v):
    ln = math.sqrt(sum(c * c for c in v[:3])) or 1.0
    return tuple(c / ln for c in v[:3])


def transform_attributes(m, positions, normals=None, tangents=None):
    """Positions, normals and tangents through a node-style matrix (rows). -> (positions, normals, tangents, determinant)."""
    nm, det = _normal_matrix(m)
    pos = [_xf_point(m, v) for v in positions]
    nrm = [_norm(_xf_dir(nm, v)) for v in normals] if normals else None
    tan = [_norm(_xf_dir(m, v)) + (v[3] if det > 0 else -v[3],) for v in tangents] if tangents else None
    return pos, nrm, tan, det


def _influences(doc, buffers, at, nv):
    """Per vertex: [(joint slot of the skin, weight)] over every JOINTS_n / WEIGHTS_n set, weights above 0 only."""
    out = [[] for _ in range(nv)]
    k = 0
    while f"JOINTS_{k}" in at:
        if f"WEIGHTS_{k}" not in at:
            raise GltfError(f"JOINTS_{k} without WEIGHTS_{k}")
        for o, js, ws in zip(out, _accessor(doc, buffers, at[f"JOINTS_{k}"]), _accessor(doc, buffers, at[f"WEIGHTS_{k}"])):
            o.extend((int(j), float(w)) for j, w in zip(js, ws) if w > 0)
        k += 1
    if not k:
        raise GltfError("a skinned primitive has no JOINTS_0")
    return out


def _morphs(doc, buffers, mesh, prim):
    """{target's name: [position delta per vertex]}. The names are `extras.targetNames` of the mesh or the primitive, as Blender and other tools write them; a target with no position delta
    changes nothing this format can hold. Deltas of normals and tangents are dropped: the game moves positions only."""
    targets = prim.get("targets")
    if not targets:
        return {}
    names = (prim.get("extras") or {}).get("targetNames") or (mesh.get("extras") or {}).get("targetNames") or []
    out = {}
    for i, t in enumerate(targets):
        if "POSITION" not in t:
            continue
        name = names[i] if i < len(names) else f"target{i}"
        out[name] = [tuple(float(c) for c in v) for v in _accessor(doc, buffers, t["POSITION"])]
    return out


def world_matrices(nodes):
    """Every node's rest transform from the scene root (rows)."""
    parent = [None] * len(nodes)
    for i, n in enumerate(nodes):
        for c in n.get("children", []):
            parent[c] = i
    out = [None] * len(nodes)

    def world(i):
        chain = []
        while i is not None and out[i] is None:
            chain.append(i)
            i = parent[i]
        m = out[i] if i is not None else None
        for j in reversed(chain):
            local = _node_matrix(nodes[j])
            m = local if m is None else _mat_mul(m, local)
            out[j] = m

    for i in range(len(nodes)):
        world(i)
    return out


def _skins(doc, buffers):
    out = []
    for i, sk in enumerate(doc.get("skins", [])):
        ibm = None
        if "inverseBindMatrices" in sk:
            ibm = [[[m[c * 4 + r] for c in range(4)] for r in range(4)] for m in _accessor(doc, buffers, sk["inverseBindMatrices"])]
            if len(ibm) != len(sk["joints"]):
                raise GltfError(f"skin {i}: {len(ibm)} inverse bind matrices for {len(sk['joints'])} joints")
        out.append({"name": sk.get("name") or f"skin{i}", "joints": list(sk["joints"]), "ibm": ibm})
    return out


def read(path):
    """-> {"primitives": [{"material": index|None, "material_name", "positions", "normals"|None, "tangents"|None,
    "uvs"|None, "colors"|None, "indices", "ancestors", "morphs"}], "materials": [names], "skins", "node_names", "node_extras", "world"}
    in glTF space (right-handed, y up, metres). `ancestors` lists the nodes above the primitive's node, nearest first.
    A primitive of a skinned node has "skin" (index into "skins") and "influences", and its attributes (morphs too) stay in mesh space."""
    doc, buffers = _load(path)
    scene = doc.get("scenes", [{}])[doc.get("scene", 0)] if doc.get("scenes") else {"nodes": list(range(len(doc.get("nodes", []))))}
    prims = []

    def visit(ni, parent, above):
        node = doc["nodes"][ni]
        m = _mat_mul(parent, _node_matrix(node))
        if "mesh" in node and not str(node.get("name", "")).startswith(REFERENCE):
            skin = node.get("skin")
            mesh = doc["meshes"][node["mesh"]]
            for p in mesh["primitives"]:
                if p.get("mode", 4) != 4:
                    raise GltfError("only triangle lists are supported")
                at = p["attributes"]
                raw_pos = _accessor(doc, buffers, at["POSITION"])
                raw_nrm = _accessor(doc, buffers, at["NORMAL"]) if "NORMAL" in at else None
                raw_tan = _accessor(doc, buffers, at["TANGENT"]) if "TANGENT" in at else None
                if skin is None:
                    pos, nrm, tan, det = transform_attributes(m, raw_pos, raw_nrm, raw_tan)
                else:  # bound later: the file's skin decides the bind pose, not the node
                    pos, nrm, tan, det = [tuple(v) for v in raw_pos], raw_nrm and [tuple(v) for v in raw_nrm], raw_tan and [tuple(v) for v in raw_tan], 1.0
                uv = [v[:2] for v in _accessor(doc, buffers, at["TEXCOORD_0"])] if "TEXCOORD_0" in at else None
                col = None
                if "COLOR_0" in at:
                    col = [tuple(v) + (1.0,) * (4 - len(v)) for v in _accessor(doc, buffers, at["COLOR_0"])]
                if "indices" in p:
                    idx = [v[0] for v in _accessor(doc, buffers, p["indices"])]
                else:
                    idx = list(range(len(pos)))
                if det < 0:  # mirrored node: keep the triangles facing outwards
                    for t in range(0, len(idx) - 2, 3):
                        idx[t + 1], idx[t + 2] = idx[t + 2], idx[t + 1]
                mi = p.get("material")
                mname = doc["materials"][mi].get("name", f"material{mi}") if mi is not None and doc.get("materials") else None
                morphs = _morphs(doc, buffers, mesh, p)
                if skin is None:
                    morphs = {k: [_xf_dir(m, v) for v in deltas] for k, deltas in morphs.items()}
                prim = {"material": mi, "material_name": mname, "positions": pos, "normals": nrm,
                        "tangents": tan, "uvs": uv, "colors": col, "indices": idx, "ancestors": above, "morphs": morphs}
                if skin is not None:
                    prim["skin"] = skin
                    prim["influences"] = _influences(doc, buffers, at, len(pos))
                prims.append(prim)
        for c in node.get("children", []):
            visit(c, m, [ni] + above)

    ident = [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]]
    for ni in scene.get("nodes", []):
        visit(ni, ident, [])
    if not prims:
        raise GltfError("no mesh primitives in the default scene")
    nodes = doc.get("nodes", [])
    return {"primitives": prims, "materials": [m.get("name", "") for m in doc.get("materials", [])], "skins": _skins(doc, buffers),
            "node_names": [n.get("name") or f"node{i}" for i, n in enumerate(nodes)], "node_extras": [n.get("extras") or {} for n in nodes],
            "world": world_matrices(nodes), "file": Path(path).name}
