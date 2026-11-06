#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Checks worn models against your installed game. Reads the game, writes nothing but its report and temporary glTF files under --out.

    python3 tools/modelcook/tests/game_worn_check.py items       every vanity item and its look through tools/kfc: decoded and encoded again, byte for byte
    python3 tools/modelcook/tests/game_worn_check.py morphs      every model with blend shapes: exported to glTF, cooked, its deltas compared with the game's
    python3 tools/modelcook/tests/game_worn_check.py table       the blend shape table in tools/kfc against the game's executable
    python3 tools/modelcook/tests/game_worn_check.py attach      every rigid hat: exported without its armature's offset and attached to the head, against the game's vertices
    python3 tools/modelcook/tests/game_worn_check.py blender     the player's skeleton into Blender, a piece made there, out through Blender's exporter and cooked, against Blender's vertices

Options: --game DIR, --out DIR (default $KILN_OUT/worncheck). Prints a summary; the details go to OUT/<command>.json.
"""
import argparse
import json
import math
import os
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent), str(HERE.parent.parent / "kfc"), str(HERE.parent.parent / "animcook")]

from cook import skin as S, steps  # noqa: E402
from kfc import mesh  # noqa: E402
from kfc.archive import ContentHash, Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402
from kfc.util import guid_text  # noqa: E402

GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
ZERO = "00000000-0000-0000-0000-000000000000"


def open_game(game):
    k, t = Kfc(game), TypeDb.from_exe(Path(game) / "enshrouded.exe")
    return k, t, Decoder(t, max_array=1 << 26)


def vanity_items(k, t, dec):
    th = t.get("keen::ItemInfo")["hash"]
    for key in k.resources_of_type(th):
        v = dec.decode(th, k.read(key))
        if "IsVanityItem" in (v.get("flags") or []):
            yield key, v


def cmd_items(a):
    k, t, dec = open_game(a.game)
    enc = Encoder(t)
    th, mh, vh = t.get("keen::ItemInfo")["hash"], t.get("keen::RenderModel")["hash"], t.get("keen::ecs::TemplateResource")["hash"]
    totals = {"items": 0, "items_identical": 0, "models": 0, "models_identical": 0, "visual_entities": 0, "visual_entities_identical": 0, "per_body_entries": 0}
    odd, seen = [], set()

    def through(type_hash, key, label):
        raw = k.read(key)
        same = enc.encode(type_hash, dec.decode(type_hash, raw)) == raw
        if not same:
            odd.append(label)
        return same

    # every item that has a visual entity, as the rigid armour items do, not only vanity
    for key in k.resources_of_type(th):
        v = dec.decode(th, k.read(key))
        eq = v["equipment"]
        if str(eq["slot"]).startswith("Armour_") and eq.get("visualEntity") not in (None, ZERO):
            vk = ResourceKey(eq["visualEntity"], vh, 0)
            if k.has(vk) and vk.guid not in seen:
                seen.add(vk.guid)
                totals["visual_entities"] += 1
                totals["visual_entities_identical"] += through(vh, vk, f"visual {v['debugName']}")
    seen = set()
    for key, v in vanity_items(k, t, dec):
        totals["items"] += 1
        totals["items_identical"] += through(th, key, f"item {v['debugName']}")
        eq = v["equipment"]
        models = [eq.get("visualModel")] + [m["model"] for m in eq.get("visualModels") or []]
        totals["per_body_entries"] += len(eq.get("visualModels") or [])
        for g in models:
            if g in (None, ZERO) or g in seen:
                continue
            seen.add(g)
            mk = ResourceKey(g, mh, 0)
            if k.has(mk):
                totals["models"] += 1
                totals["models_identical"] += through(mh, mk, f"model {g}")
    print(json.dumps(totals, indent=1))
    if odd:
        print("differ:", odd[:20])
    write_report(a, "items", {"totals": totals, "odd": odd})
    return 0 if not odd else 1


def cmd_table(a):
    raw = (Path(a.game) / "enshrouded.exe").read_bytes()
    ok = bad = 0
    for i, (name, g) in enumerate(mesh.BLEND_SHAPES.items()):
        if i == 0:
            first = raw.find(_guid_raw(g))
        at = first + 16 * i
        if guid_text(raw[at:at + 16]) == g:
            ok += 1
        else:
            bad += 1
            print(f"{name}: tools/kfc says {g}, the executable {guid_text(raw[at:at + 16])}")
    t = TypeDb.from_exe(Path(a.game) / "enshrouded.exe")
    flags = [v["name"] for v in t.get("keen::BlendShapeFlags")["values"]]
    same_order = flags == list(mesh.BLEND_SHAPES)
    print(json.dumps({"shapes": ok + bad, "match": ok, "same_order_as_reflection": same_order}))
    return 0 if not bad and same_order else 1


def _guid_raw(g):
    from kfc.util import guid_bytes
    return guid_bytes(g)


def _groups(d):
    seen = {}
    for x in d["meshes"]:
        seen.setdefault(x["vertexDataByteOffset"], x)
    return seen


def cmd_morphs(a):
    k, t, dec = open_game(a.game)
    rm = t.get("keen::RenderModel")
    totals = {"models": 0, "models_ok": 0, "shapes": 0, "groups_with_data": 0, "vertices": 0, "deltas_byte_identical": 0, "deltas_different": 0, "worst_abs_m": 0.0, "morph_vertices_nonzero": 0}
    odd = []
    t0 = time.time()
    for key in k.resources_of_type(rm["hash"]):
        raw = k.read(key)
        d = dec.decode(rm, raw)
        if not d["blendShapes"]:
            continue
        if a.limit and totals["models"] >= a.limit:
            break
        totals["models"] += 1
        m = mesh.load_render_model(k, t, key.guid)
        hier = mesh.load_hierarchy(k, t, d["hierarchy"])
        skel = mesh.skeleton_rh(hier)
        try:
            with tempfile.TemporaryDirectory() as td:
                files = []
                for lod in range(len(d["lods"])):
                    p = Path(td) / f"lod{lod}.gltf"
                    mesh.write_gltf(mesh.to_rh(mesh.triangles(m, lod)), p, skeleton=skel)
                    files.append(str(p))
                target = steps.game_target(k, t, d["hierarchy"])
                produced, _, report = steps.model(k, t, files, key.guid, material=None, pixel_sizes=[x["pixelSize"] for x in d["lods"]], stem="m", subdir="c", target=target)
        except (S.SkinError, steps.StepError, steps.RM.CookError) as e:
            odd.append({"model": d["debugName"], "error": str(e)})
            continue
        c = dec.decode(rm, produced["c/m.bin"])
        blob = produced["c/m.blob"]
        om = mesh.RenderModel(guid="c", desc=c, data=blob)
        names_game = [mesh.BLEND_SHAPE_NAMES.get(b["id"]) for b in d["blendShapes"]]
        names_ours = [mesh.BLEND_SHAPE_NAMES.get(b["id"]) for b in c["blendShapes"]]
        fine = set(names_game) == set(names_ours) and c["blendShapeMask"] == (1 << len(names_ours)) - 1
        have = mesh.blend_groups(m)
        pairs = {}
        for x, y in zip(d["meshes"], c["meshes"]):
            pairs.setdefault(x["vertexDataByteOffset"], (x, y))
        worst = 0.0
        for off, (x, y) in pairs.items():
            if off not in have:
                for si, _ in enumerate(names_ours):
                    if any(any(v) for v in mesh.blend_of(om, y, si)):
                        fine = False  # a group with no data gets zeros
                continue
            totals["groups_with_data"] += 1
            for gi, name in enumerate(names_game):
                ours = mesh.blend_of(om, y, names_ours.index(name))
                game = mesh.blend_of(m, x, gi)
                totals["shapes"] += 1
                totals["vertices"] += len(game)
                totals["morph_vertices_nonzero"] += sum(1 for v in game if any(v))
                if ours == game:
                    totals["deltas_byte_identical"] += 1
                else:
                    totals["deltas_different"] += 1
                    worst = max(worst, max(abs(p - q) for u, v in zip(ours, game) for p, q in zip(u, v)))
        totals["worst_abs_m"] = max(totals["worst_abs_m"], worst)
        failed = [n for n, ok in report["checks"].items() if not ok and n != "winding_like_vanilla"]
        if worst > 1e-6:
            fine = False
        if failed or report["problems"] or not fine:
            odd.append({"model": d["debugName"], "failed": failed, "problems": report["problems"][:3], "worst": worst})
        else:
            totals["models_ok"] += 1
    totals["seconds"] = round(time.time() - t0, 1)
    print(json.dumps(totals, indent=1))
    for o in odd[:20]:
        print(o)
    write_report(a, "morphs", {"totals": totals, "odd": odd})
    return 0 if totals["models_ok"] == totals["models"] else 1


def cmd_attach(a):
    """The game's own hats are rigid on the head. Moved so that their origin is the joint, then attached, they must come back to the same vertices, on the same one joint."""
    k, t, dec = open_game(a.game)
    rm = t.get("keen::RenderModel")
    target = steps.game_target(k, t, steps.PLAYER_SKELETON)
    head = steps.attach_joint(k, t, target, "Head")
    b = target.bind[head]
    print(f"Head is joint {head}, at {b[0][3]:.4f}, {b[1][3]:.4f}, {b[2][3]:.4f} in game space")
    slots = steps.slot_joints(k, t, steps.PLAYER_SKELETON)
    print("slots:", json.dumps(slots))
    totals = {"models": 0, "ok": 0, "worst_m": 0.0}
    odd = []
    seen = set()
    for key, v in vanity_items(k, t, dec):
        g = v["equipment"].get("visualModel")
        if g in (None, ZERO) or g in seen:
            continue
        seen.add(g)
        mk = ResourceKey(g, rm["hash"], 0)
        if not k.has(mk):
            continue
        d = dec.decode(rm, k.read(mk))
        if d["skinningMap"] != [head] or d["hierarchy"] != steps.PLAYER_SKELETON:
            continue
        totals["models"] += 1
        m = mesh.load_render_model(k, t, g)
        tri = mesh.to_rh(mesh.triangles(m, 0))
        for s in tri.submeshes:
            s["positions"] = [(p[0] + b[0][3], p[1] - b[1][3], p[2] - b[2][3]) for p in s["positions"]]  # x is mirrored in glTF space
            s.pop("joints", None), s.pop("weights", None)
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "hat.gltf"
            mesh.write_gltf(tri, p)
            produced, _, report = steps.model(k, t, [str(p)], g, material=None, stem="m", subdir="c", target=target, attach="Head")
        c = dec.decode(rm, produced["c/m.bin"])
        om = mesh.RenderModel(guid="c", desc=c, data=produced["c/m.blob"])
        worst = 0.0
        lo = d["lods"][0]
        for x, y in zip(d["meshes"][lo["meshOffset"]:lo["meshOffset"] + lo["meshCount"]], c["meshes"]):
            for u, w in zip(mesh.decode_group(m, x)["positions"], mesh.decode_group(om, y)["positions"]):
                worst = max(worst, math.dist(u, w))
        totals["worst_m"] = max(totals["worst_m"], worst)
        failed = [n for n, ok in report["checks"].items() if not ok and n != "winding_like_vanilla"]  # some of the game's models have mixed winding
        if c["skinningMap"] == [head] and worst < 1e-4 and not failed and not report["problems"]:
            totals["ok"] += 1
        else:
            odd.append({"model": d["debugName"], "map": c["skinningMap"], "worst": worst, "failed": failed})
    print(json.dumps(totals))
    for o in odd[:10]:
        print(o)
    write_report(a, "attach", {"totals": totals, "odd": odd, "slots": slots})
    return 0 if totals["ok"] == totals["models"] and totals["models"] else 1


def cmd_blender(a):
    k, t, dec = open_game(a.game)
    work = Path(tempfile.mkdtemp())
    reference, glb, ev = work / "player.glb", work / "helmet.glb", work / "helmet.eval.json"
    print(json.dumps(steps.player_reference(k, t, reference, "male")))
    r = subprocess.run(["blender", "-b", "--factory-startup", "--python", str(HERE / "blender_worn_author.py"), "--", str(reference), str(glb), str(ev)], capture_output=True, text=True, timeout=300)
    print("\n".join(x for x in r.stdout.splitlines() if x.startswith("BLENDER") or "Error" in x or "Traceback" in x))
    if not ev.exists():
        print(r.stdout[-2000:], r.stderr[-2000:])
        return 1
    want = json.loads(ev.read_text())
    target = steps.game_target(k, t, steps.PLAYER_SKELETON)
    g = uuid.uuid4()
    mat = next(iter(k.resources_of_type(t.get("keen::RenderMaterialResource")["hash"]))).guid
    produced, _, report = steps.model(k, t, [str(glb)], None, guid=str(g), mode="new", name="helmet", stem="m", subdir="c", material=mat, target=target)
    c = dec.decode(t.get("keen::RenderModel"), produced["c/m.bin"])
    om = mesh.RenderModel(guid="c", desc=c, data=produced["c/m.blob"])
    tri = mesh.to_rh(mesh.triangles(om, 0))
    got = [(p, s["morphs"]["Female"][i]) for s in tri.submeshes for i, p in enumerate(s["positions"])]
    print("warnings:", report["warnings"])
    print("joints:", c["skinningMap"], "shapes:", [mesh.BLEND_SHAPE_NAMES.get(b["id"]) for b in c["blendShapes"]], "checks failed:", [n for n, ok in report["checks"].items() if not ok])
    worst_p = worst_d = 0.0
    for p, d in zip(want["positions"], want["female"]):
        q, e = min(got, key=lambda x: math.dist(x[0], p))
        worst_p, worst_d = max(worst_p, math.dist(p, q)), max(worst_d, math.dist(d, e))
    joints = sorted(int(n[1:].split("_")[0]) for n in (want["head"], want["neck"]))
    ok = worst_p < 1e-4 and worst_d < 1e-4 and report["ok"] and c["skinningMap"] == joints and [mesh.BLEND_SHAPE_NAMES.get(b["id"]) for b in c["blendShapes"]] == ["Female"]
    print(f"{len(want['positions'])} of Blender's vertices against the cook: positions within {worst_p * 1000:.4f} mm, Female deltas within {worst_d * 1000:.4f} mm: {'ok' if ok else 'FAILED'}")
    write_report(a, "blender", {"worst_position_m": worst_p, "worst_female_delta_m": worst_d, "ok": ok, "warnings": report["warnings"]})
    return 0 if ok else 1


def write_report(a, name, doc):
    out = Path(a.out or (Path(os.environ.get("KILN_OUT", tempfile.gettempdir())) / "worncheck"))
    out.mkdir(parents=True, exist_ok=True)
    (out / f"{name}.json").write_text(json.dumps(doc, indent=1) + "\n")
    print(f"report: {out / (name + '.json')}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=("items", "morphs", "table", "attach", "blender"))
    ap.add_argument("--game", default=str(GAME))
    ap.add_argument("--out")
    ap.add_argument("--limit", type=int)
    a = ap.parse_args()
    return {"items": cmd_items, "morphs": cmd_morphs, "table": cmd_table, "attach": cmd_attach, "blender": cmd_blender}[a.command](a)


if __name__ == "__main__":
    sys.exit(main())
