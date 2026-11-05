#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Checks the skin writer against the skinned models of your installed game. Reads the game, writes nothing but its report and temporary glTF files under --out.

    python3 tools/modelcook/tests/game_skin_check.py stream                  every skinned model's stream through the writer, against the game's bytes
    python3 tools/modelcook/tests/game_skin_check.py cook [--match TEXT] [--limit N] [--jobs N]
                                                                             whole models: export to glTF, cook, decode, compare, pose with one of their clips

Options: --game DIR, --out DIR (default $KILN_OUT/skincheck). Prints a summary; the details go to OUT/<command>.json.
"""
import argparse
import json
import multiprocessing
import os
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent), str(HERE.parent.parent / "kfc"), str(HERE.parent.parent / "animcook")]

import skindeform as D  # noqa: E402
from cook import skin as S, steps  # noqa: E402
from kfc import anim, mesh  # noqa: E402
from kfc.archive import ContentHash, Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
ZERO = "00000000-0000-0000-0000-000000000000"


def open_game(game):
    k, t = Kfc(game), TypeDb.from_exe(game / "enshrouded.exe")
    return k, t, Decoder(t, max_array=1 << 26), t.get("keen::RenderModel")


def cmd_stream(a):
    k, t, dec, rm = open_game(Path(a.game))
    totals = {"models": 0, "vertices": 0, "bytes": 0, "models_identical": 0, "models_same_map": 0, "bytes_different": 0, "bytes_different_used": 0, "vertices_changed_meaning": 0,
              "unused_slots_keeping_an_index": 0, "models_with_chain_skipped": 0}
    odd = []
    t0 = time.time()
    for key in k.resources_of_type(rm["hash"]):
        d = dec.decode(rm, k.read(key))
        sd = d["skinningData"]
        if not sd["count"]:
            continue
        if d["chain"]:  # trailing map entries of 0 stand for joints the chain simulation moves: a model joint means more than its map entry, and the writer doesn't write chains
            totals["models_with_chain_skipped"] += 1
            continue
        blob = k.read_content(ContentHash(d["renderData"]["hash"]))
        game = blob[sd["start"]:sd["start"] + sd["count"]]
        smap = d["skinningMap"]
        verts = []
        for v in range(0, len(game), 8):
            pairs = [(smap[game[v + s]], game[v + 4 + s] / 255) for s in range(4) if game[v + 4 + s]]
            reduced, _ = S.reduce_influences(pairs)
            verts.append(S.quantize(reduced))
        ours = S.joint_map([[{"influences": verts}]])
        same_map = ours == smap
        index = {j: i for i, j in enumerate(smap)}  # ambiguous only where the map repeats a joint, and used joints don't repeat
        for i, j in reversed(list(enumerate(smap))):
            index[j] = i
        out = S.encode_stream(verts, index)
        diff = sum(x != y for x, y in zip(out, game))
        used = changed = kept = 0
        if diff:
            for v in range(0, len(game), 8):
                a_, b_ = out[v:v + 8], game[v:v + 8]
                if a_ == b_:
                    continue
                kept += sum(1 for s in range(4) if not b_[4 + s] and b_[s])
                x = sorted((smap[a_[s]], a_[4 + s]) for s in range(4) if a_[4 + s])
                y = {}
                for s in range(4):
                    if b_[4 + s]:
                        y[smap[b_[s]]] = y.get(smap[b_[s]], 0) + b_[4 + s]
                y = sorted(y.items())
                if x != y:
                    changed += 1
                used += sum(1 for s in range(4) if b_[4 + s] and a_[s] != b_[s])
        totals["models"] += 1
        totals["vertices"] += len(verts)
        totals["bytes"] += len(game)
        totals["models_identical"] += out == game
        totals["models_same_map"] += same_map
        totals["bytes_different"] += diff
        totals["bytes_different_used"] += used
        totals["vertices_changed_meaning"] += changed
        totals["unused_slots_keeping_an_index"] += kept
        if not same_map or changed or used:
            odd.append({"model": d["debugName"], "same_map": same_map, "map": len(smap), "our_map": len(ours), "used_bytes_differ": used, "vertices_changed_meaning": changed,
                        "cloth": bool(d["cloth"]), "chain": bool(d["chain"])})
    totals["seconds"] = round(time.time() - t0, 1)
    write_report(a, "stream", {"totals": totals, "odd": odd})
    print(json.dumps(totals, indent=1))
    print(f"models where the map or a used byte differs: {len(odd)} (details in the report)")
    return 0 if not totals["vertices_changed_meaning"] and not totals["bytes_different_used"] else 1


def write_report(a, name, doc):
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / f"{name}.json").write_text(json.dumps(doc, indent=1))


def clips_by_hierarchy(k, t):
    ja = t.get("keen::JointAnimation")
    out = {}
    for key in k.resources_of_type(ja["hash"]):
        raw = k.read(key)
        out.setdefault(int.from_bytes(raw[:8], "little"), []).append((len(raw), key.guid))
    return out


def largest_clip(k, t, hierarchy, by_hash):
    """The clip with the most data on this skeleton: likely to move the most joints."""
    cands = by_hash.get(anim.hierarchy_hash(hierarchy["jointNames"]))
    return max(cands)[1] if cands else None


_STATE = {}


def _worker_init(game):
    _STATE["ctx"] = open_game(Path(game))
    _STATE["clips"] = clips_by_hierarchy(_STATE["ctx"][0], _STATE["ctx"][1])
    _STATE["game"] = game


def cook_one(guid):
    k, t, dec, rm = _STATE["ctx"]
    m = mesh.load_render_model(k, t, guid)
    d = m.desc
    hier = mesh.load_hierarchy(k, t, d["hierarchy"])
    skel = mesh.skeleton_rh(hier)
    r = {"guid": guid, "name": d["debugName"], "joints": d["jointCount"], "skeleton_joints": d["hierarchyJointCount"], "lods": len(d["lods"]), "globalScale": d["globalScale"],
         "vertices": d["skinningData"]["count"] // 8, "cloth": bool(d["cloth"]), "chain": bool(d["chain"]), "blend": bool(d["blendShapes"])}
    t0 = time.time()
    with tempfile.TemporaryDirectory() as td:
        files = []
        for lod in range(len(d["lods"])):
            p = Path(td) / f"lod{lod}.gltf"
            mesh.write_gltf(mesh.to_rh(mesh.triangles(m, lod)), p, skeleton=skel)
            files.append(str(p))
        try:
            target = steps.game_target(k, t, d["hierarchy"])
            produced, _, report = steps.model(k, t, files, guid, material=None, pixel_sizes=[x["pixelSize"] for x in d["lods"]], stem="m", subdir="c", target=target)
        except (S.SkinError, steps.StepError, steps.RM.CookError) as e:
            r["error"] = str(e)
            return r
    data, blob = produced["c/m.bin"], produced["c/m.blob"]
    c = dec.decode(rm, data)
    r["seconds"] = round(time.time() - t0, 1)
    r["checks_failed"] = [n for n, ok in report["checks"].items() if not ok]
    r["problems"] = report["problems"]
    r["same_map"] = c["skinningMap"] == d["skinningMap"]
    r["same_global_scale"] = c["globalScale"] == d["globalScale"]
    r["same_lods"] = [x["meshCount"] for x in c["lods"]] == [x["meshCount"] for x in d["lods"]] and [x["pixelSize"] for x in c["lods"]] == [x["pixelSize"] for x in d["lods"]]
    ours_i = blob[c["indexData"]["start"]:c["indexData"]["start"] + c["indexData"]["count"]]
    game_i = m.data[d["indexData"]["start"]:d["indexData"]["start"] + d["indexData"]["count"]]
    r["indices_identical"] = ours_i == game_i
    r["shape"] = {"blob_game": len(m.data), "blob_ours": len(blob), "same_ranges": all(c[n]["count"] == d[n]["count"] for n in ("vertexData", "skinningData", "indexData"))}
    r["streams"] = compare_streams(m, c, blob)
    r["pose"] = compare_pose(k, t, m, hier, c, blob, _STATE["clips"])
    return r


def compare_streams(m, c, blob):
    d = m.desc
    out = {"same_shape": False}
    if d["skinningData"]["count"] != c["skinningData"]["count"] or d["vertexData"]["count"] != c["vertexData"]["count"]:
        return out
    out["same_shape"] = True
    gs, os_ = m.data[d["skinningData"]["start"]:][:d["skinningData"]["count"]], blob[c["skinningData"]["start"]:][:c["skinningData"]["count"]]
    used_diff = 0
    for v in range(0, len(gs), 8):
        if gs[v:v + 8] != os_[v:v + 8]:
            used_diff += sum(1 for s in range(4) if gs[v + 4 + s] and (gs[v + s] != os_[v + s] or gs[v + 4 + s] != os_[v + 4 + s]))
    out["skin_bytes_different"] = sum(a != b for a, b in zip(gs, os_))
    out["skin_used_slots_different"] = used_diff
    gv, ov = m.data[d["vertexData"]["start"]:][:d["vertexData"]["count"]], blob[c["vertexData"]["start"]:][:c["vertexData"]["count"]]
    out["vertex_bytes_identical"] = gv == ov
    sc = c["positionScale"]
    step = max(sc["x"], sc["y"], sc["z"]) / ((1 << 21) - 1)
    om = mesh.RenderModel(guid="o", desc=c, data=blob)
    worst = 0.0
    worst_n = 1.0
    seen = set()
    for a, b in zip(d["meshes"], c["meshes"]):
        if a["vertexDataByteOffset"] in seen:
            continue
        seen.add(a["vertexDataByteOffset"])
        ga, gb = mesh.decode_group(m, a), mesh.decode_group(om, b)
        for p, q in zip(ga["positions"], gb["positions"]):
            worst = max(worst, abs(p[0] - q[0]), abs(p[1] - q[1]), abs(p[2] - q[2]))
        for p, q in zip(ga["normals"], gb["normals"]):
            la = sum(x * x for x in p[:3]) ** 0.5
            lb = sum(x * x for x in q[:3]) ** 0.5
            if la > 1e-3 and lb > 1e-3:
                worst_n = min(worst_n, sum(x * y for x, y in zip(p[:3], q[:3])) / (la * lb))
    out["max_position_error_m"] = worst
    out["quantization_step_m"] = step
    out["min_normal_dot"] = worst_n
    return out


def compare_pose(k, t, m, hier, c, blob, clips):
    guid = largest_clip(k, t, hier, clips)
    if not guid:
        return {"clip": None}
    ja = t.get("keen::JointAnimation")
    clip = anim.parse(k.read(ResourceKey(guid, ja["hash"], 0)))
    mh = t.get("keen::ModelHierarchyResource")
    dp = anim.parse_default_pose(k.read(ResourceKey(hier["hierarchyId"], mh["hash"], 0)))
    worst = moved = 0.0
    rest = D.cooked_posed(m.desc, m.data, hier, D.rest_pose(hier))
    for u in (0.3, 0.7):
        world = D.pose_of_clip(hier, anim.sample_frame(clip, min(u * clip.frame_count, clip.frame_count - 1 + 0.999), dp))
        a = D.cooked_posed(m.desc, m.data, hier, world)
        b = D.cooked_posed(c, blob, hier, world)
        if all(len(x) == len(y) for x, y in zip(a, b)):
            worst = max(worst, D.max_distance(a, b))
            moved = max(moved, D.max_distance(a, rest))
    return {"clip": guid, "frames": clip.frame_count, "max_distance_m": worst, "largest_move_from_rest_m": moved}


def cmd_cook(a):
    game = Path(a.game)
    k, t, dec, rm = open_game(game)
    guids = []
    for key in k.resources_of_type(rm["hash"]):
        d = dec.decode(rm, k.read(key))
        if d["skinningData"]["count"] and d["hierarchy"] != ZERO and (not a.match or a.match in d["debugName"]):
            guids.append((d["skinningData"]["count"], key.guid, d["debugName"]))
    guids.sort()
    if a.limit and a.limit < len(guids):  # an even spread over model sizes
        guids = [guids[i * len(guids) // a.limit] for i in range(a.limit)]
    print(f"{len(guids)} models", flush=True)
    jobs = a.jobs or max(1, (os.cpu_count() or 2) // 2)
    results = []
    with multiprocessing.get_context("spawn").Pool(jobs, _worker_init, (str(game),)) as pool:
        for i, r in enumerate(pool.imap_unordered(cook_one, [g[1] for g in guids])):
            results.append(r)
            if i % 10 == 9:
                print(f"{i + 1} done", flush=True)
    bad = [r for r in results if r.get("error") or r.get("problems") or r.get("checks_failed")]
    good = [r for r in results if r not in bad]
    summary = {"models": len(results), "cooked": len(good), "refused_or_failed": len(bad), "vertices": sum(r["vertices"] for r in good),
               "same_map": sum(r["same_map"] for r in good), "same_global_scale": sum(r["same_global_scale"] for r in good), "indices_identical": sum(r["indices_identical"] for r in good),
               "same_ranges": sum(r["shape"]["same_ranges"] for r in good),
               "skin_used_slots_different": sum(r["streams"].get("skin_used_slots_different", 0) for r in good),
               "max_position_error_m": max((r["streams"].get("max_position_error_m", 0) for r in good), default=0),
               "largest_position_error_in_quanta": max((r["streams"].get("max_position_error_m", 0) / r["streams"]["quantization_step_m"] for r in good if r["streams"].get("quantization_step_m")), default=0),
               "min_normal_dot": min((r["streams"].get("min_normal_dot", 1) for r in good), default=1),
               "worst_pose_distance_m": max((r["pose"].get("max_distance_m", 0) for r in good), default=0),
               "posed_models": sum(1 for r in good if r["pose"].get("clip")),
               "largest_move_m": max((r["pose"].get("largest_move_from_rest_m", 0) for r in good), default=0)}
    write_report(a, a.report or "cook", {"summary": summary, "models": sorted(results, key=lambda r: r["name"])})
    print(json.dumps(summary, indent=1))
    for r in bad[:12]:
        print("REFUSED" if r.get("error") else "FAILED", r["name"], (r.get("error") or str(r.get("checks_failed")) + str(r.get("problems")))[:300])
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=str(GAME))
    ap.add_argument("--out", default=str(Path(os.environ.get("KILN_OUT", tempfile.gettempdir())) / "skincheck"))
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("stream")
    p = sub.add_parser("cook")
    p.add_argument("--match")
    p.add_argument("--limit", type=int)
    p.add_argument("--jobs", type=int)
    p.add_argument("--report")
    a = ap.parse_args(argv)
    return {"stream": cmd_stream, "cook": cmd_cook}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
