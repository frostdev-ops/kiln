# SPDX-License-Identifier: MIT
"""Offline validation of a cooked RenderModel: decode with tools/kfc and compare with the input and a vanilla model."""
import math

from kfc import mesh as kmesh
from kfc.archive import ResourceKey
from kfc.blob import Decoder
from kfc.blobwrite import Encoder

from . import rendermodel as RM, skin as S


LEFT_EMPTY = ("cloth.", "chain.", "collider.", "sdf.", "skinnedTree.", "blendShapes.", "sleeves.", "foliageAnimationShaderData.")  # optional data a cooked model doesn't carry


def _keys(v, prefix=""):
    """Set of dotted field paths (lists collapsed) to compare structure, not values."""
    out = set()
    if isinstance(v, dict):
        for k, x in v.items():
            out.add(prefix + k)
            out |= _keys(x, prefix + k + ".")
    elif isinstance(v, list) and v:
        out |= _keys(v[0], prefix + "[].")
    return out


def _check_skin(d, blob, types_target, checks, problems, report):
    """The skin stream read back: the format's rules, and the bind pose the game would draw."""
    groups = {}
    for ms in d["meshes"]:
        groups.setdefault(ms["vertexDataByteOffset"], ms)
    total = sum(ms["vertexCount"] for ms in groups.values())
    sd = d["skinningData"]
    checks["skin_stream_covers_every_vertex"] = sd["count"] == 8 * total
    offs = True
    n = 0
    for off in sorted(groups):
        offs = offs and groups[off]["skinVertexOffset"] == n
        n += groups[off]["vertexCount"]
    checks["skin_offsets_cumulative"] = offs
    smap = d["skinningMap"]
    checks["joint_map_consistent"] = len(smap) == d["jointCount"] and bool(smap) and smap == sorted(set(smap)) and smap[-1] < d["hierarchyJointCount"]
    stream = blob[sd["start"]:sd["start"] + sd["count"]]
    sums = descending = indexed = True
    used = set()
    empty_clean = True
    for v in range(0, len(stream), 8):
        idx, w = stream[v:v + 4], stream[v + 4:v + 8]
        sums = sums and sum(w) == S.WEIGHT_SUM
        descending = descending and w[0] >= w[1] >= w[2] >= w[3]
        for i, x in zip(idx, w):
            if x:
                indexed = indexed and i < len(smap)
                used.add(i)
            else:
                empty_clean = empty_clean and i == 0
    checks["weights_sum_255"] = sums
    checks["weights_descending"] = descending
    checks["joint_indices_in_map"] = indexed
    checks["unused_slots_zero"] = empty_clean
    checks["every_map_entry_carries_weight"] = len(used) == len(smap)
    report["skin"] = {"joints": len(smap), "skeleton_joints": d["hierarchyJointCount"], "skeleton": d["hierarchy"], "vertices": total, "stream_bytes": sd["count"],
                      "globalScale": d["globalScale"]}
    if types_target is None:
        return
    checks["skeleton_matches"] = types_target.guid == d["hierarchy"] and len(types_target) == d["hierarchyJointCount"]
    # Drawn at the skeleton's bind pose every joint's pose x inverse bind is the identity, so what the shader computes is the stored vertex. What can still be
    # wrong is a vertex far from the joints that move it: the heaviest joint's distance tells a good binding from a scrambled one (docs/re/models.md 5).
    m = kmesh.RenderModel(guid="cooked", desc=d, data=blob)
    influences = S.decode_stream(stream)
    dist = []
    for off in sorted(groups):
        ms = groups[off]
        g = kmesh.decode_group(m, ms)
        for i, q in enumerate(g["positions"]):
            j = smap[influences[ms["skinVertexOffset"] + i][0][0]]
            b = types_target.bind[j]
            dist.append(math.dist(q, (b[0][3], b[1][3], b[2][3])))
    dist.sort()
    report["skin"]["heaviest_joint_distance_m"] = {"median": dist[len(dist) // 2], "p95": dist[int(len(dist) * 0.95)], "max": dist[-1]}
    bb = d["boundingBox"]
    size = max(bb["max"][a] - bb["min"][a] for a in "xyz")
    checks["vertices_near_their_joints"] = dist[len(dist) // 2] <= max(size / 2, 0.01)


def validate(kfc, types, data, blob, against=None, gltf_docs=None, target=None):
    """kfc may be None (no game files): the game-dependent checks (materials, vanilla comparison) are skipped.
    target: the skin.Target a skinned model was bound to, for the checks against its skeleton."""
    checks = {}
    problems = []
    th = types.get("keen::RenderModel")["hash"]
    dec = Decoder(types)
    d = dec.decode(th, data)
    checks["decodes"] = True
    checks["reencodes_byte_exact"] = Encoder(types).encode(th, d) == data
    h = d["renderData"]["hash"]
    checks["content_hash_matches_blob"] = (h["size"], h["hash0"], h["hash1"], h["hash2"]) == RM.content_hash(blob)
    in_blob = all(not d[x]["count"] or d[x]["start"] + d[x]["count"] <= len(blob) for x in
                  ("vertexData", "indexData", "meshletData", "skinningData", "blendShapeData"))
    checks["ranges_inside_blob"] = in_blob
    checks["has_lods_and_meshes"] = bool(d["lods"]) and bool(d["meshes"])
    m = kmesh.RenderModel(guid="cooked", desc=d, data=blob)
    ok_idx = True
    for i, ms in enumerate(d["meshes"]):
        idx = kmesh.indices_of(m, ms)
        if ms["indexCount"] % 3 or (idx and max(idx) >= ms["vertexCount"]):
            ok_idx = False
            problems.append(f"mesh {i}: bad indices")
        if ms["materialIndex"] >= len(d["materials"]):
            ok_idx = False
            problems.append(f"mesh {i}: material index out of range")
    checks["indices_valid"] = ok_idx
    if kfc is not None:
        mth = types.get("keen::RenderMaterialResource")["hash"]
        checks["materials_exist_in_game"] = all(kfc.has(ResourceKey(x["material"], mth, 0)) for x in d["materials"])
    tri0 = kmesh.triangles(m, 0)
    w = kmesh.winding_check(tri0)
    checks["winding_like_vanilla"] = w is not None and w < 0.05
    report = {"checks": checks, "problems": problems, "lods": len(d["lods"]), "meshes": len(d["meshes"]),
              "vertices_lod0": tri0.vertex_count, "triangles_lod0": tri0.triangle_count,
              "ccw_agree_game_space": w, "bounding_box": d["boundingBox"]}
    if d["skinningData"]["count"]:
        _check_skin(d, blob, target, checks, problems, report)
    if against and kfc is not None:
        vk = ResourceKey(against, th, 0)
        vd = dec.decode(th, kfc.read(vk))
        vm = kmesh.load_render_model(kfc, types, against)
        same_fields = all(x.startswith(LEFT_EMPTY) or x in ("meshes.[]", "lods.[]") for x in _keys(vd) - _keys(d))
        checks["same_fields_as_vanilla"] = same_fields
        checks["same_vertex_format_as_vanilla"] = {(x["vertexFormat"], x["vertexStride"], x["positionQuantization"])
                                                   for x in d["meshes"]} <= {(x["vertexFormat"], x["vertexStride"], x["positionQuantization"])
                                                                              for x in vd["meshes"]}
        checks["same_render_data_category"] = vd["renderData"]["category"] == d["renderData"]["category"]
        report["vanilla"] = {"guid": vk.guid, "name": vd["debugName"], "lods": len(vd["lods"]),
                             "vertices_lod0": kmesh.triangles(vm, 0).vertex_count,
                             "ccw_agree_game_space": kmesh.winding_check(kmesh.triangles(vm, 0)),
                             "bounding_box": vd["boundingBox"]}
    if gltf_docs:
        sc = d["positionScale"]
        step = max(sc["x"], sc["y"], sc["z"]) / ((1 << 21) - 1)
        worst_p, worst_n, worst_uv = 0.0, 1.0, 0.0
        for li, doc in enumerate(gltf_docs):
            rh = kmesh.to_rh(kmesh.triangles(m, li))
            if d["skinningData"]["count"]:
                lod = d["lods"][li]
                for got, ms in zip(rh.submeshes, d["meshes"][lod["meshOffset"]:lod["meshOffset"] + lod["meshCount"]]):
                    st = d["skinningData"]["start"] + 8 * ms["skinVertexOffset"]
                    got["raw_joints"] = [blob[st + 8 * v:st + 8 * v + 4] for v in range(ms["vertexCount"])]
                    got["raw_weights"] = [blob[st + 8 * v + 4:st + 8 * v + 8] for v in range(ms["vertexCount"])]
            for src, got in zip(doc["primitives"], rh.submeshes):
                for a, b in zip(src["positions"], got["positions"]):
                    worst_p = max(worst_p, max(abs(a[i] - b[i]) for i in range(3)))
                if src.get("normals"):
                    for a, b in zip(src["normals"], got["normals"]):
                        ln = math.sqrt(sum(x * x for x in b[:3])) or 1
                        worst_n = min(worst_n, sum(a[i] * b[i] for i in range(3)) / ln)
                if src.get("uvs"):
                    for a, b in zip(src["uvs"], got["uvs"]):
                        worst_uv = max(worst_uv, abs(a[0] - b[0]), abs(a[1] - b[1]))
                if src["indices"] != got["indices"]:
                    problems.append("indices differ from the glTF")
                if "influences" in src:
                    smap = d["skinningMap"]
                    if [[(j, w) for j, w in inf] for inf in src["influences"]] != [[(smap[j], w) for j, w in zip(js, ws) if w] for js, ws in zip(got["raw_joints"], got["raw_weights"])]:
                        problems.append("the skin stream differs from the bound influences")
        checks["positions_within_quantization"] = worst_p <= 1.01 * step
        checks["normals_match"] = worst_n > 0.995
        checks["uvs_match_f16"] = worst_uv < 2e-3
        report["roundtrip"] = {"max_position_error_m": worst_p, "quantization_step_m": step,
                               "min_normal_dot": worst_n, "max_uv_error": worst_uv}
    report["ok"] = all(checks.values()) and not problems
    return report
