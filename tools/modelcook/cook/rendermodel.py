# SPDX-License-Identifier: MIT
"""Build a keen::RenderModel resource + its renderData content blob from triangles (format: docs/re/models.md).

Output layout (as the game's own cooked models):
    renderData = [group 0: P | N | T | C | UV streams][group 1 ...] [skin stream, when skinned] [u16 indices of every mesh, LOD 0 first]
    vertexData = {0, sum of group bytes}, skinningData = {after them, 8 x vertices}, indexData follows, others empty.
Each glTF primitive (one material each) becomes one vertex group and one RenderModelMesh per LOD. Skinned primitives with the same vertices share a group, as the
game's LODs do.
"""
import hashlib
import struct
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent.parent / "kfc"))

from kfc import mesh as kmesh  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402

from . import geometry as G, skin as S  # noqa: E402

# The value every vanilla RenderModel.renderData carries (10470 of 10470, client 1285601).
RENDER_DATA_CATEGORY = 1497973154
RENDER_DATA_OFFSET = 0x110  # RenderModel.renderData (ResourceContentReference; .hash is its first 16 bytes)
MAX_VERTICES = 65535  # u16 indices


class CookError(Exception):
    pass


def content_hash(data):
    """kiln.assets' ContentHash for a mod blob: {size, sha256[0:4], [4:8], [8:12]} (little-endian u32s)."""
    d = hashlib.sha256(data).digest()
    return (len(data),) + struct.unpack_from("<3I", d)


def prepare(prim):
    """Fill missing attributes (glTF space), then convert to game space."""
    P = prim["positions"]
    if len(P) > MAX_VERTICES:
        raise CookError(f"primitive with {len(P)} vertices: split it (u16 indices, at most {MAX_VERTICES})")
    if len(prim["indices"]) % 3:
        raise CookError("index count is not a multiple of 3")
    if any(i >= len(P) for i in prim["indices"]):
        raise CookError("index out of range")
    p = dict(prim)
    if not p.get("normals"):
        p["normals"] = G.compute_normals(P, p["indices"])
    if not p.get("uvs"):
        p["uvs"] = [(0.0, 0.0)] * len(P)
    if not p.get("tangents"):
        p["tangents"] = G.compute_tangents(P, p["normals"], p["uvs"], p["indices"])
    if not p.get("colors"):
        p["colors"] = [(1.0, 1.0, 1.0, 1.0)] * len(P)
    return G.to_game(p)


def quantization(all_positions):
    """positionOffset/positionScale the way the game's cooker picks them (checked on vanilla models: the bounding
    box grown on every side by 20 % of its largest half extent)."""
    mn, mx = G.bounds(all_positions)
    half = max((mx[i] - mn[i]) / 2 for i in range(3))
    m = 0.2 * half if half > 0 else 0.01
    off = tuple(mn[i] - m for i in range(3))
    sc = tuple(mx[i] - mn[i] + 2 * m for i in range(3))
    return off, sc, mn, mx


def encode_group(p, off, sc):
    n = len(p["positions"])
    out = bytearray()
    for q in p["positions"]:
        out += struct.pack("<II", *kmesh.pack_pos21(q, sc, off))
    for v in p["normals"]:
        out += struct.pack("<I", kmesh.pack_1010102(v[0], v[1], v[2], 1))
    for v in p["tangents"]:
        out += struct.pack("<I", kmesh.pack_1010102(v[0], v[1], v[2], 1 if v[3] >= 0 else -1))
    for c in p["colors"]:
        out += bytes(max(0, min(255, int(round(x * 255)))) for x in c[:4])
    for uv in p["uvs"]:
        out += struct.pack("<ee", uv[0], uv[1])
    assert len(out) == 24 * n
    return bytes(out)


def _vec(v):
    return {"x": float(v[0]), "y": float(v[1]), "z": float(v[2])}


def build(lods, materials, base, name, pixel_sizes=None, skin=None):
    """lods: [[prepared primitive (game space), ...] per LOD]; materials: [{material guid, renderEffectId,
    materialFlags}] indexed by primitive["material_slot"]; base: a decoded vanilla RenderModel (dict) whose other
    fields are kept (empty cloth/sdf are forced). skin: {"hierarchy": skeleton GUID, "hierarchyJointCount", "globalScale"} for a skinned model,
    whose primitives carry "influences" (cook/skin.py). Returns (desc dict, renderData bytes)."""
    allp = [q for lod in lods for p in lod for q in p["positions"]]
    off, sc, mn, mx = quantization(allp)
    smap = S.joint_map(lods) if skin else []
    if len(smap) > S.MAX_MODEL_JOINTS:
        raise CookError(f"{len(smap)} joints carry weight: a model's skin stream addresses {S.MAX_MODEL_JOINTS} (u8)")
    model_index = {j: i for i, j in enumerate(smap)}
    groups = bytearray()
    skin_stream = bytearray()
    known_groups = {}
    indices = []
    meshes = []
    lod_desc = []
    vcount_total = 0
    for li, lod in enumerate(lods):
        first = len(meshes)
        for p in lod:
            vb = encode_group(p, off, sc)
            sb = S.encode_stream(p["influences"], model_index) if skin else b""
            c, r = G.bounding_sphere(p["positions"])
            group = known_groups.get((vb, sb)) if skin else None
            if group is None:
                group = known_groups[(vb, sb)] = (len(groups), vcount_total)
                groups += vb
                skin_stream += sb
                vcount_total += len(p["positions"])
            meshes.append({
                "boundingSphere": {"center": _vec(c), "radius": float(r)},
                "materialIndex": p["material_slot"],
                "vertexCount": len(p["positions"]),
                "indexOffset": len(indices),
                "indexCount": len(p["indices"]),
                "meshletOffset": 0, "meshletCount": 0,
                "vertexDataByteOffset": group[0],
                "blendShapeByteOffset": 0,
                "skinVertexOffset": group[1],
                "vertexFormat": "PNTCT", "vertexStride": 24, "clothMeshIndex": 0,
                "positionQuantization": "x21y21z21_unorm",
            })
            indices += p["indices"]
        ps = pixel_sizes[li] if pixel_sizes else (0.0 if li == 0 else 0.01 * (1.6 ** li))
        lod_desc.append({"meshOffset": first, "meshCount": len(meshes) - first, "pixelSize": float(ps)})
    vdata = bytes(groups)
    sdata = bytes(skin_stream)
    idata = struct.pack(f"<{len(indices)}H", *indices)
    blob = vdata + sdata + idata
    c, r = G.bounding_sphere(allp)
    radius = max((q[0] ** 2 + q[1] ** 2 + q[2] ** 2) ** 0.5 for q in allp)
    h = content_hash(blob)
    d = dict(base)
    d.update({
        "debugName": name,
        "boundingBox": {"min": _vec(mn), "max": _vec(mx)},
        "boundingSphere": {"center": _vec(c), "radius": float(r)},
        "positionScale": _vec(sc), "positionOffset": _vec(off),
        "wholeModelRadius": float(radius),
        "materials": materials,
        "meshlets": [], "meshes": meshes, "lods": lod_desc,
        "foliageAnimationShaderData": None,
        "hierarchy": "00000000-0000-0000-0000-000000000000",
        "blendShapes": [], "blendShapeMask": 0, "globalScale": 1.0, "hasCardClusterLod": False,
        "sdf": {"parts": []},
        "skinnedTree": {"jointMatrix": [], "jointParent": [], "jointSpringData": [], "leafJoints": [],
                        "inverseTransformTree": []},
        "skinningMap": smap, "cloth": None, "chain": None,
        "collider": {"hasSkinning": False, "ignoreClothCollider": False, "spheres": [], "capsules": [], "instances": []},
        "sleeves": [], "jointCount": len(smap), "hierarchyJointCount": skin["hierarchyJointCount"] if skin else 0,
        "renderData": {"hash": {"size": h[0], "hash0": h[1], "hash1": h[2], "hash2": h[3]},
                       "category": {"value": RENDER_DATA_CATEGORY}, "bundleId": 0, "sortKey": 0},
        "indexData": {"start": len(vdata) + len(sdata), "count": len(idata)},
        "meshletData": {"start": (len(blob) + 3) & ~3 if skin else len(blob), "count": 0},
        "vertexData": {"start": 0, "count": len(vdata)},
        "skinningData": {"start": len(vdata) if skin else 0, "count": len(sdata)},
        "blendShapeData": {"start": 0, "count": 0},
    })
    if skin:
        d["hierarchy"] = skin["hierarchy"]
        d["globalScale"] = float(skin.get("globalScale", 1.0))
    return d, blob


def encode(types, desc):
    data = Encoder(types).encode("keen::RenderModel", desc)
    # the patch offset kiln.assets uses must hold the hash we computed
    h = desc["renderData"]["hash"]
    assert data[RENDER_DATA_OFFSET:RENDER_DATA_OFFSET + 16] == struct.pack("<4I", h["size"], h["hash0"], h["hash1"], h["hash2"])
    return data
