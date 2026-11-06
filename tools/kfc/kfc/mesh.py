# SPDX-License-Identifier: MIT
"""keen::RenderModel geometry: decode and export (OBJ, glTF 2.0). Format: docs/re/models.md.

A RenderModel resource (same GUID as the keen::Model an ItemInfo references) describes meshes, LODs and materials;
its `renderData` ResourceContentReference names one content blob that holds every GPU buffer, addressed by the
Range32 fields (byte start, byte count): vertexData, skinningData, indexData, meshletData, blendShapeData.

Vertex data is planar per vertex group (a group = meshes sharing `vertexDataByteOffset`), one stream per attribute,
in the order of the vertexFormat letters, each `vertexCount` elements long:

    P   8 B  positionQuantization x21y21z21_unorm: lo = u32[0], hi = u32[1]
             x = lo >> 11, y = (lo & 0x7FF) << 10 | (hi >> 21) & 0x3FF, z = hi & 0x1FFFFF   (21-bit unorm)
             pos = positionOffset + q / (2^21 - 1) * positionScale
             positionQuantization none: 4 x float16 (x, y, z, 1)
    N   4 B  normal  10:10:10:2 snorm (x bits 0-9, y 10-19, z 20-29, w 30-31; w = +1)
    T   4 B  tangent 10:10:10:2 snorm, w = handedness (+1 / -1)
    C   4 B  colour  RGBA8 unorm
    T   4 B  texcoord 2 x float16 (u, v)

Indices are u16, local to the mesh's vertex group, `indexOffset`/`indexCount` in elements from indexData.start.

Handedness: model and world space are left-handed: x right, y up, z forward. Two pieces of evidence: the player
skeleton faces +z (eye, brow and jaw joints at z +0.09..0.12 m) with the HandR slot's joint at +x; the world map
has x east and z north (map pixel y = 10240 - z). With the component cross product, (b - a) x (c - a) points
against the vertex normals for every triangle of every model checked. Exports convert to glTF's right-handed frame
by mirroring x (positions, normals, tangents; tangent w flips) and keep the index order. Then the triangles turn
counter-clockwise around their normals, and the front of a character keeps facing +Z, as glTF wants. modelcook
applies the same mapping (it is its own inverse).
"""
import json
import math
import struct
from dataclasses import dataclass, field
from pathlib import Path

from .archive import ContentHash, ResourceKey
from .blob import Decoder

Q21 = (1 << 21) - 1

STREAMS = {  # vertexFormat -> [(attribute, bytes)]
    "P": [("position", 8)],
    "PC": [("position", 8), ("color", 4)],
    "PCT": [("position", 8), ("color", 4), ("texcoord", 4)],
    "PNT": [("position", 8), ("normal", 4), ("texcoord", 4)],
    "PNTCT": [("position", 8), ("normal", 4), ("tangent", 4), ("color", 4), ("texcoord", 4)],
}

# keen::BlendShapeFlags, in order: bit i of a BlendShapeMask. The client's fixed table (client 0x14152a898) turns a raised flag into the BlendShapeId a model lists.
BLEND_SHAPES = {
    "Female": "3ec83c3b-82b6-4731-a7b9-4463f9b5c76d",
    "Hat": "fc7fc617-3863-4b8d-ae43-082152713256",
    "Hood": "2b3fc521-07c4-440d-be70-307bc0b35898",
    "Jaw01": "43fde1e6-0196-4ba9-97db-beeea2e26e56",
    "Jaw02": "552be9b4-f21a-46d2-af20-3e8a401d6ab4",
    "Jaw03": "5ad93866-5d4a-4acf-b6bb-7d921d19c775",
    "Jaw04": "531aeae7-0dbf-4038-93f9-dba2ec60adbc",
    "Jaw05": "0ccc503e-0583-4444-99ea-57c11be270ee",
    "Jaw06": "b41d570e-dc19-44a7-9672-11bef496d2fe",
    "Jaw07": "17237349-2a2e-4660-b080-ed5fad00068d",
    "Jaw08": "ba84be17-0280-4f45-bbb1-6fdd0a730420",
    "Jaw09": "880334c2-bef9-4ccd-84d1-66b0ebdc502b",
    "Jaw10": "b5b9ec46-176a-4668-80e8-1671c1adfebd",
    "Jaw11": "36753a8b-3409-4948-a779-cbb94da8dcc0",
    "Jaw12": "c9ec3a54-b035-4bed-9126-967415d7509d",
    "Jaw13": "55880850-79de-4c24-91c3-fdb4b42f34d9",
    "Jaw14": "b6473d9c-a42a-4cdb-999c-3bd080c2d4ee",
    "Jaw15": "7a97c809-9a26-449e-9297-724d4d7a4db2",
    "Jaw16": "d1d46eec-5da9-4899-b6f9-2e617e0ec670",
    "NoArmor": "c14fbcd5-1577-493e-a348-b0da20b83d6c",
    "Shield": "5a272f2b-5397-48aa-8543-cb3f4968c1a8",
    "Tight": "09ea4cf7-5e1b-4335-9689-f4dbb61dc199",
    "ArmsTight01": "7b972fea-d31c-4833-ae90-a0eb2bcefe71",
    "LegsBeltTight": "199337da-538e-42db-bac1-34106dcc39e8",
    "LegsFeetTight": "4abf7c82-2c16-4757-9fe4-b5e7f6b158a6",
    "LegsFeetTight01": "178e21c0-3b72-4c50-ae2e-a59ade68555e",
}
BLEND_SHAPE_NAMES = {g: n for n, g in BLEND_SHAPES.items()}


@dataclass
class RenderModel:
    guid: str
    desc: dict          # decoded keen::RenderModel
    data: bytes         # the renderData content blob

    @property
    def lods(self):
        return self.desc["lods"]

    @property
    def meshes(self):
        return self.desc["meshes"]


@dataclass
class Tris:
    """Triangles of one LOD: flat arrays per submesh (positions are model space, metres)."""
    submeshes: list = field(default_factory=list)  # [{material, positions, normals, uvs, colors, tangents, indices}]

    @property
    def vertex_count(self):
        return sum(len(s["positions"]) for s in self.submeshes)

    @property
    def triangle_count(self):
        return sum(len(s["indices"]) // 3 for s in self.submeshes)


def load_render_model(kfc, types, guid):
    th = types.get("keen::RenderModel")["hash"]
    key = ResourceKey(guid, th, 0)
    desc = Decoder(types).decode(th, kfc.read(key))
    data = kfc.read_content(ContentHash(desc["renderData"]["hash"]))
    return RenderModel(guid=key.guid, desc=desc, data=data)


def summary(m):
    d = m.desc
    lines = [f"{m.guid} {d['debugName']}: {len(d['meshes'])} meshes, {len(d['lods'])} lods, "
             f"{len(d['materials'])} materials, joints {d['jointCount']}/{d['hierarchyJointCount']}, "
             f"renderData {d['renderData']['hash']['size']} B"]
    for i, lod in enumerate(d["lods"]):
        ms = d["meshes"][lod["meshOffset"]:lod["meshOffset"] + lod["meshCount"]]
        lines.append(f"  lod {i} pixelSize {lod['pixelSize']:.4f}: " + ", ".join(
            f"[mat {x['materialIndex']} {x['vertexCount']} v {x['indexCount'] // 3} t {x['vertexFormat']}]" for x in ms))
    return "\n".join(lines)


# ---- attribute codecs (also used by modelcook to encode) ----
def unpack_pos21(lo, hi, scale, offset):
    x = lo >> 11
    y = ((lo & 0x7FF) << 10) | ((hi >> 21) & 0x3FF)
    z = hi & 0x1FFFFF
    return (offset[0] + x / Q21 * scale[0], offset[1] + y / Q21 * scale[1], offset[2] + z / Q21 * scale[2])


def pack_pos21(p, scale, offset):
    q = []
    for i in range(3):
        u = (p[i] - offset[i]) / scale[i] if scale[i] else 0.0
        q.append(max(0, min(Q21, int(round(u * Q21)))))
    x, y, z = q
    lo = (x << 11) | (y >> 10)
    hi = z | ((y & 0x3FF) << 21)
    return lo, hi


def _sn10(v):
    v &= 0x3FF
    if v >= 512:
        v -= 1024
    return max(v / 511.0, -1.0)


def unpack_1010102(u):
    w = (u >> 30) & 3
    w = w - 4 if w >= 2 else w
    return (_sn10(u), _sn10(u >> 10), _sn10(u >> 20), float(w))


def pack_1010102(x, y, z, w):
    def e(f):
        i = int(round(max(-1.0, min(1.0, f)) * 511))
        return i & 0x3FF
    return e(x) | (e(y) << 10) | (e(z) << 20) | ((int(w) & 3) << 30)


def _half(b, o):
    return struct.unpack_from("<e", b, o)[0]


def decode_group(m, mesh):
    """Vertex attributes of the group a mesh belongs to -> dict of lists."""
    d = m.desc
    fmt = mesh["vertexFormat"]
    if fmt not in STREAMS:
        raise ValueError(f"vertex format {fmt} not supported")
    n = mesh["vertexCount"]
    base = d["vertexData"]["start"] + mesh["vertexDataByteOffset"]
    sc = (d["positionScale"]["x"], d["positionScale"]["y"], d["positionScale"]["z"])
    of = (d["positionOffset"]["x"], d["positionOffset"]["y"], d["positionOffset"]["z"])
    out = {}
    o = base
    for attr, size in STREAMS[fmt]:
        buf = m.data[o:o + n * size]
        if attr == "position":
            if mesh["positionQuantization"] == "x21y21z21_unorm":
                out["positions"] = [unpack_pos21(*struct.unpack_from("<II", buf, 8 * i), sc, of) for i in range(n)]
            else:
                out["positions"] = [(_half(buf, 8 * i), _half(buf, 8 * i + 2), _half(buf, 8 * i + 4)) for i in range(n)]
        elif attr in ("normal", "tangent"):
            vals = [unpack_1010102(struct.unpack_from("<I", buf, 4 * i)[0]) for i in range(n)]
            out[attr + "s"] = vals
        elif attr == "color":
            out["colors"] = [tuple(c / 255.0 for c in buf[4 * i:4 * i + 4]) for i in range(n)]
        elif attr == "texcoord":
            out["uvs"] = [(_half(buf, 4 * i), _half(buf, 4 * i + 2)) for i in range(n)]
        o += n * size
    if o - base != n * mesh["vertexStride"]:
        raise ValueError(f"streams {o - base} B != vertexCount x stride {n * mesh['vertexStride']}")
    return out


def indices_of(m, mesh):
    st = m.desc["indexData"]["start"] + 2 * mesh["indexOffset"]
    return list(struct.unpack_from(f"<{mesh['indexCount']}H", m.data, st))


def skin_of(m, mesh):
    """Per vertex: 4 skeleton joint indices (through skinningMap) and 4 weights (0..1) of the mesh's group.
    The stream holds 8 bytes per vertex: 4 x u8 model-joint indices, then 4 x u8 weights (they sum to 255)."""
    d = m.desc
    st = d["skinningData"]["start"] + 8 * mesh["skinVertexOffset"]
    smap = d["skinningMap"]
    joints, weights = [], []
    for v in range(mesh["vertexCount"]):
        b = m.data[st + 8 * v:st + 8 * v + 8]
        joints.append(tuple(smap[i] if i < len(smap) else 0 for i in b[:4]))
        weights.append(tuple(w / 255.0 for w in b[4:8]))
    return joints, weights


def blend_groups(m):
    """The vertex groups that hold blend shape data, by vertexDataByteOffset. A group's `blendShapeByteOffset` is its start inside blendShapeData, and a group with no data also says 0,
    so the range's size tells which of the groups at 0 owns it: the data groups add up to it."""
    d = m.desc
    n = len(d["blendShapes"])
    total = d["blendShapeData"]["count"]
    if not n or not total:
        return set()
    groups = {}
    for x in d["meshes"]:
        groups.setdefault(x["vertexDataByteOffset"], x)
    others = [g for g in groups.values() if g["blendShapeByteOffset"]]
    rest = total - sum(g["vertexCount"] * 6 * n for g in others)
    at_zero = [o for o, g in sorted(groups.items()) if not g["blendShapeByteOffset"] and g["vertexCount"] * 6 * n == rest]
    return {o for o, g in groups.items() if g["blendShapeByteOffset"]} | set(at_zero[:1])


def blend_of(m, mesh, shape):
    """One blend shape's position deltas of a mesh's group, in model space (metres), or None when the group has no data. Per group: every vertex of the first shape, then of the next,
    3 x f16 each (the vertex shader's reading: start + (shape x vertexCount + vertex) x 6)."""
    d = m.desc
    if mesh["vertexDataByteOffset"] not in blend_groups(m):
        return None
    n = mesh["vertexCount"]
    st = d["blendShapeData"]["start"] + mesh["blendShapeByteOffset"] + shape * n * 6
    v = struct.unpack_from(f"<{3 * n}e", m.data, st)
    return [v[i:i + 3] for i in range(0, 3 * n, 3)]


def _quat_mat(q):
    x, y, z, w = q
    return [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]


def _mul4(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def _inv_affine(m):
    """Inverse of an affine 4x4 (rotation x scale + translation)."""
    a = [r[:3] for r in m[:3]]
    det = (a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1]) - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0])
           + a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]))
    inv = [[(a[(j + 1) % 3][(i + 1) % 3] * a[(j + 2) % 3][(i + 2) % 3] - a[(j + 1) % 3][(i + 2) % 3] * a[(j + 2) % 3][(i + 1) % 3]) / det
            for j in range(3)] for i in range(3)]
    t = [m[i][3] for i in range(3)]
    it = [-sum(inv[i][k] * t[k] for k in range(3)) for i in range(3)]
    return [inv[0] + [it[0]], inv[1] + [it[1]], inv[2] + [it[2]], [0, 0, 0, 1]]


def _matrix43(m):
    """A decoded keen::Matrix43 (the columns x, y, z, then the translation) as a 4x4."""
    rot, pos = m["rot"], m["pos"]
    return [[rot[c][a] for c in "xyz"] + [pos[a]] for a in "xyz"] + [[0.0, 0.0, 0.0, 1.0]]


def skeleton_rh(h):
    """A decoded keen::ModelHierarchyResource -> joints in glTF's right-handed frame (x mirrored):
    [{"parent", "t", "r" (x, y, z, w), "s", "global" 4x4, "name_hash"}], with "matrix" (4x4, parent-relative) in place of t, r and s when a joint's rest isn't its default pose.
    The rest pose is what the mesh was bound in, the inverse of skinToJointMatrices. On 248 of the game's 257 skeletons that is defaultPoseJoints (parent-relative) within 0.001, and
    those are written as such; the others start every clip from another pose (docs/re/models.md 5)."""
    out = []
    mirror = (-1, 1, 1, 1)
    for i in range(h["jointCount"]):
        j = h["defaultPoseJoints"][i]
        p, q, sc = j["position"], j["orientation"], j["scale"]
        t = (-p["x"], p["y"], p["z"])
        r = (q["x"], -q["y"], -q["z"], q["w"])  # mirror x: R -> M R M
        s3 = (sc["x"], sc["y"], sc["z"])
        rm = _quat_mat(r)
        local = [[rm[a][b] * s3[b] for b in range(3)] + [t[a]] for a in range(3)] + [[0, 0, 0, 1]]
        par = h["jointParentIndices"][i]
        par = None if par == 0xFFFF else par
        glob = local if par is None else _mul4(out[par]["global"], local)
        rec = {"parent": par, "t": t, "r": r, "s": s3, "global": glob, "name_hash": h["jointNames"][i] if i < len(h["jointNames"]) else 0}
        if i < len(h.get("skinToJointMatrices", ())):
            bind = _inv_affine(_matrix43(h["skinToJointMatrices"][i]))
            bind = [[bind[a][b] * mirror[a] * mirror[b] for b in range(4)] for a in range(4)]
            if max(abs(bind[a][b] - glob[a][b]) for a in range(4) for b in range(4)) > 1e-3:
                rec["global"] = bind
                rec["matrix"] = bind if par is None else _mul4(_inv_affine(out[par]["global"]), bind)
        out.append(rec)
    return out


def load_hierarchy(kfc, types, guid):
    th = types.get("keen::ModelHierarchyResource")["hash"]
    return Decoder(types).decode(th, kfc.read(ResourceKey(guid, th, 0)))


def triangles(m, lod=0):
    d = m.desc
    if not d["lods"]:
        sel = d["meshes"]
    else:
        l = d["lods"][lod]
        sel = d["meshes"][l["meshOffset"]:l["meshOffset"] + l["meshCount"]]
    tri = Tris()
    for mesh in sel:
        g = decode_group(m, mesh)
        g["indices"] = indices_of(m, mesh)
        if d.get("skinningData", {}).get("count"):
            g["joints"], g["weights"] = skin_of(m, mesh)
        g["material"] = d["materials"][mesh["materialIndex"]]["material"] if d["materials"] else None
        g["materialIndex"] = mesh["materialIndex"]
        morphs = {}
        for i, b in enumerate(d.get("blendShapes") or []):
            deltas = blend_of(m, mesh, i)
            if deltas is not None:
                morphs[BLEND_SHAPE_NAMES.get(b["id"], b["id"])] = deltas
        if morphs:
            g["morphs"] = morphs
        tri.submeshes.append(g)
    return tri


def winding_check(tri):
    """Fraction of triangles whose geometric normal (counter-clockwise rule) agrees with the vertex normals."""
    agree = total = 0
    for s in tri.submeshes:
        P, N, I = s["positions"], s.get("normals"), s["indices"]
        if not N:
            continue
        for t in range(0, len(I) - 2, 3):
            a, b, c = P[I[t]], P[I[t + 1]], P[I[t + 2]]
            u = (b[0] - a[0], b[1] - a[1], b[2] - a[2])
            v = (c[0] - a[0], c[1] - a[1], c[2] - a[2])
            fn = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
            if fn == (0, 0, 0):
                continue
            vn = [sum(N[I[t + k]][j] for k in range(3)) for j in range(3)]
            total += 1
            agree += (fn[0] * vn[0] + fn[1] * vn[1] + fn[2] * vn[2]) > 0
    return agree / total if total else None


def to_rh(tri):
    """Left-handed game space -> right-handed (glTF/OBJ): x -> -x, tangent w -> -w; indices unchanged."""
    out = Tris()
    for s in tri.submeshes:
        t = dict(s)
        t["positions"] = [(-p[0], p[1], p[2]) for p in s["positions"]]
        if s.get("normals"):
            t["normals"] = [(-n[0], n[1], n[2], n[3]) for n in s["normals"]]
        if s.get("tangents"):
            t["tangents"] = [(-n[0], n[1], n[2], -n[3]) for n in s["tangents"]]
        if s.get("morphs"):
            t["morphs"] = {k: [(-v[0], v[1], v[2]) for v in deltas] for k, deltas in s["morphs"].items()}
        out.submeshes.append(t)
    return out


def write_obj(tri, path):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = ["# Kiln tools/kfc export (decoded from game data; do not commit)"]
    base = 1
    for si, s in enumerate(tri.submeshes):
        lines.append(f"o submesh{si}_mat{s['materialIndex']}")
        for p in s["positions"]:
            lines.append(f"v {p[0]:.6f} {p[1]:.6f} {p[2]:.6f}")
        for uv in s.get("uvs", []):
            lines.append(f"vt {uv[0]:.6f} {1.0 - uv[1]:.6f}")
        for n in s.get("normals", []):
            lines.append(f"vn {n[0]:.5f} {n[1]:.5f} {n[2]:.5f}")
        I = s["indices"]
        hv, hn = bool(s.get("uvs")), bool(s.get("normals"))
        for t in range(0, len(I) - 2, 3):
            f = []
            for k in range(3):
                i = I[t + k] + base
                f.append(f"{i}/{i if hv else ''}/{i if hn else ''}".rstrip("/") if (hv or hn) else str(i))
            lines.append("f " + " ".join(f))
        base += len(s["positions"])
    path.write_text("\n".join(lines) + "\n")


def write_gltf(tri, path, name="model", skeleton=None, joint_names=None):
    """glTF 2.0 with an embedded buffer (data URI; a .glb path gets the binary container): positions, normals, tangents, uv0, colour, u32 indices, morph targets;
    one primitive per submesh. `tri` must already be right-handed (to_rh). With `skeleton` (skeleton_rh()), the joints become nodes "j<index>" (or joint_names[index]) and a skin; submeshes
    with joints get JOINTS_0 / WEIGHTS_0."""
    import base64
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    blob = bytearray()
    views, accessors, prims, materials = [], [], [], []
    mat_index = {}
    morph_names = []
    for s in tri.submeshes:
        morph_names += [k for k in s.get("morphs", {}) if k not in morph_names]

    def add(data, target, comp, typ, count, mn=None, mx=None):
        while len(blob) % 4:
            blob.append(0)
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), "target": target})
        blob.extend(data)
        a = {"bufferView": len(views) - 1, "componentType": comp, "count": count, "type": typ}
        if mn is not None:
            a["min"], a["max"] = mn, mx
        accessors.append(a)
        return len(accessors) - 1

    for s in tri.submeshes:
        P = s["positions"]
        attrs = {"POSITION": add(b"".join(struct.pack("<3f", *p) for p in P), 34962, 5126, "VEC3", len(P),
                                 [min(p[i] for p in P) for i in range(3)], [max(p[i] for p in P) for i in range(3)])}
        if s.get("normals"):
            ns = []
            for n in s["normals"]:
                ln = math.sqrt(n[0] ** 2 + n[1] ** 2 + n[2] ** 2) or 1.0
                ns.append(struct.pack("<3f", n[0] / ln, n[1] / ln, n[2] / ln))
            attrs["NORMAL"] = add(b"".join(ns), 34962, 5126, "VEC3", len(P))
        if s.get("tangents"):
            attrs["TANGENT"] = add(b"".join(struct.pack("<4f", *t) for t in s["tangents"]), 34962, 5126, "VEC4", len(P))
        if s.get("colors") and any(c != (1.0, 1.0, 1.0, 1.0) for c in s["colors"]):
            attrs["COLOR_0"] = add(b"".join(struct.pack("<4f", *c) for c in s["colors"]), 34962, 5126, "VEC4", len(P))
        if s.get("uvs"):
            attrs["TEXCOORD_0"] = add(b"".join(struct.pack("<2f", *uv) for uv in s["uvs"]), 34962, 5126, "VEC2", len(P))
        if skeleton and s.get("joints"):
            attrs["JOINTS_0"] = add(b"".join(struct.pack("<4H", *j) for j in s["joints"]), 34962, 5123, "VEC4", len(P))
            attrs["WEIGHTS_0"] = add(b"".join(struct.pack("<4f", *w) for w in s["weights"]), 34962, 5126, "VEC4", len(P))
        I = s["indices"]
        ia = add(struct.pack(f"<{len(I)}I", *I), 34963, 5125, "SCALAR", len(I))
        key = s.get("material")
        if key not in mat_index:
            mat_index[key] = len(materials)
            materials.append({"name": f"keen:{key}", "pbrMetallicRoughness": {"baseColorFactor": [0.7, 0.6, 0.45, 1.0],
                                                                             "metallicFactor": 0.6, "roughnessFactor": 0.5}})
        prim = {"attributes": attrs, "indices": ia, "material": mat_index[key]}
        if morph_names:  # every primitive of a mesh has the same targets; one with no data for a shape gets zeros
            targets = []
            for k in morph_names:
                deltas = s.get("morphs", {}).get(k) or [(0.0, 0.0, 0.0)] * len(P)
                targets.append({"POSITION": add(b"".join(struct.pack("<3f", *v) for v in deltas), 34962, 5126, "VEC3", len(P),
                                                [min(v[i] for v in deltas) for i in range(3)], [max(v[i] for v in deltas) for i in range(3)])})
            prim["targets"] = targets
        prims.append(prim)
    nodes = [{"mesh": 0, "name": name}]
    roots = [0]
    skins = []
    if skeleton:
        first = len(nodes)
        for i, j in enumerate(skeleton):
            pose = {"matrix": [j["matrix"][r][c] for c in range(4) for r in range(4)]} if "matrix" in j else {"translation": list(j["t"]), "rotation": list(j["r"]), "scale": list(j["s"])}
            nodes.append({"name": (joint_names or {}).get(i, f"j{i}"), **pose, "extras": {"keen_joint": i, "keen_name_hash": j["name_hash"]}})
        for i, j in enumerate(skeleton):
            if j["parent"] is None:
                roots.append(first + i)
            else:
                nodes[first + j["parent"]].setdefault("children", []).append(first + i)
        ibm = b"".join(struct.pack("<16f", *[_inv_affine(j["global"])[r][c] for c in range(4) for r in range(4)])
                       for j in skeleton)
        skins.append({"joints": list(range(first, first + len(skeleton))),
                      "inverseBindMatrices": add(ibm, None, 5126, "MAT4", len(skeleton))})
        views[-1].pop("target", None)
        nodes[0]["skin"] = 0
    doc = {
        "asset": {"version": "2.0", "generator": "kiln tools/kfc"},
        "scene": 0, "scenes": [{"nodes": roots}], "nodes": nodes,
        "meshes": [{"name": name, "primitives": prims, **({"extras": {"targetNames": morph_names}} if morph_names else {})}], "materials": materials,
        "buffers": [{"byteLength": len(blob),
                     "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()}],
        "bufferViews": views, "accessors": accessors,
    }
    if skins:
        doc["skins"] = skins
    if path.suffix.lower() == ".glb":
        doc["buffers"] = [{"byteLength": len(blob)}]
        js = json.dumps(doc).encode()
        js += b" " * (-len(js) & 3)
        bin_ = bytes(blob) + bytes(-len(blob) & 3)
        path.write_bytes(struct.pack("<III", 0x46546C67, 2, 28 + len(js) + len(bin_)) + struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(bin_), 0x004E4942) + bin_)
    else:
        path.write_text(json.dumps(doc))
