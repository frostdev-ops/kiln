#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Builds this mod's assets on your machine: its own figure and a stick figure on one of the game's skeletons -> cook.json -> two template copies read from your installed game. Everything
this writes is git-ignored.

    python3 mods/tests/skincheck/build-assets.py [--no-override] [--game DIR]

--no-override leaves the Vukah's own model alone (the creature of our own files still works): use it when the override itself is what fails.
"""
import argparse
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent.parent
sys.path[:0] = [str(ROOT / "tools/kfc"), str(ROOT / "tools/animcook"), str(ROOT / "tools/modelcook"), str(ROOT / "tools/modelcook/samples"), str(ROOT / "tools/modelcook/tests")]

ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument("--no-override", action="store_true")
ap.add_argument("--game", default=str(Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"))
a = ap.parse_args()
GAME = Path(a.game).expanduser()
A = HERE / "assets"
(A / "src").mkdir(parents=True, exist_ok=True)
tool = str(ROOT / "tools/modelcook")
subprocess.check_call([sys.executable, tool + "/samples/rigged_figure.py", str(A / "src/figure.glb")])
subprocess.check_call([sys.executable, tool + "/samples/rigged_figure.py", str(A / "src/figure_lod1.glb"), "--lod1"])
subprocess.check_call([sys.executable, tool + "/samples/mannequin.py", str(A / "src/mannequin.gltf"), "--model", "7d9ae3f5-d713-48e3-9ee0-c053d03c7c2a", "--game", str(GAME)])
subprocess.check_call([sys.executable, tool + "/modelcook.py", "--game", str(GAME), "recipe", str(A / "cook.json"), "--update-manifest"])

import skindeform as D  # noqa: E402
from anicook.graphgen import derive  # noqa: E402
from kfc import anim, mesh  # noqa: E402
from kfc.archive import Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

k, t = Kfc(GAME), TypeDb.from_exe(GAME / "enshrouded.exe")
dec, enc = Decoder(t, max_array=1 << 26), Encoder(t)
MOD = "skincheck"
VUKAH = "d5d05217-52dc-467a-9192-26b4f661d8d9"
TEMPLATE = t.get("keen::ecs::TemplateResource")
GRAPH = json.loads((A / "cooked/figure.graph.json").read_text())
MODEL = json.loads((A / "cooked/figure.model.json").read_text())
PUPPET = ["CurrentTransform", "Actor", "ActorInput", "NetworkActor", "AnimationGraphResourceComponent2", "ServerAnimationGraphInput", "NetworkAnimationGraphInput",
          "ServerAnimationGraphComponent2", "ServerAnimationGraphSlotComponent", "Velocity", "LastTransform", "Faction"]
entries = []


def template(name, keep=None):
    """A copy of the Vukah's template on our skeleton, graph and model. Its gear hangs on slots of the Vukah's: left out."""
    v = dec.decode(TEMPLATE, k.read(ResourceKey(VUKAH, TEMPLATE["hash"], 0)))
    v["name"] = name
    out = []
    for c in v["components"]:
        nm = c["$type"].split("::")[-1]
        if nm in ("SubEntities", "HookShotAnchorSubEntity") or (keep is not None and nm not in keep):
            continue
        if nm == "AnimationGraphResourceComponent2":
            c["value"]["animationGraph"] = GRAPH["guid"]
        if nm == "ModelResource":
            c["value"]["model"] = MODEL["guid"]
        out.append(c)
    v["components"] = out
    raw = enc.encode(TEMPLATE, v)
    guid = derive(MOD, "template", name)
    (A / "cooked" / f"t_{name}.bin").write_bytes(raw)
    entries.append({"kind": "resource", "guid": guid, "type": "keen::ecs::TemplateResource", "part": 0, "file": f"cooked/t_{name}.bin", "mode": "new"})
    print(name, guid, len(raw), "B")


template("Kiln_SkinCheck_Figure")
template("Kiln_SkinCheck_Puppet", PUPPET)

man = json.loads((A / "assets.json").read_text())
keys = {e["file"] for e in entries}
man["entries"] = [e for e in man["entries"] if e["file"] not in keys] + entries
if a.no_override:
    man["entries"] = [e for e in man["entries"] if "vukah_mannequin" not in e["file"]]
(A / "assets.json").write_text(json.dumps(man, indent=2) + "\n")

# What the lab test compares the server's animation engine with: the figure held in the pose of its `hold` clip, as the client's math poses the cooked model.
ht = t.get("keen::ModelHierarchyResource")
rm = t.get("keen::RenderModel")
hraw = (A / "cooked/figure.skeleton.bin").read_bytes()
hier = dec.decode(ht, hraw)
desc = dec.decode(rm, (A / "cooked/figure.bin").read_bytes())
blob = (A / "cooked/figure.blob").read_bytes()
clip = anim.parse((A / "cooked/clip_hold.bin").read_bytes())
world = D.pose_of_clip(hier, anim.sample_frame(clip, 10.0, anim.parse_default_pose(hraw)))
side = json.loads((A / "cooked/figure.skeleton.json").read_text())
names = [j["name"] for j in side["joints"]]
posed = D.cooked_posed(desc, blob, hier, world)
m = mesh.RenderModel(guid="", desc=desc, data=blob)
lod0 = desc["meshes"][:desc["lods"][0]["meshCount"]]
samples = {}
for ms, ps in zip(lod0, posed):
    bind = mesh.decode_group(m, ms)["positions"]
    st = desc["skinningData"]["start"] + 8 * ms["skinVertexOffset"]
    for v in range(0, len(bind), 7):
        s = blob[st + 8 * v:st + 8 * v + 8]
        if s[4] == 255:
            samples.setdefault(names[desc["skinningMap"][s[0]]], []).append({"bind": list(bind[v]), "posed": list(ps[v])})
slots = {"HandR": ("hand_r", 8), "Head": ("head", 1), "Pelvis": ("pelvis", 3)}
expect = {"holdState": GRAPH["states"]["hold"]["id"], "modelGuid": MODEL["guid"], "skeleton": MODEL["skeleton"], "slots": {}}
for slot, (joint, index) in slots.items():
    j = names.index(joint)
    S = hier["skinToJointMatrices"][j]
    expect["slots"][slot] = {"index": index, "joint": joint, "skinToJoint": [[S["rot"][c][r] for c in "xyz"] + [S["pos"][r]] for r in "xyz"],
                             "pose": [world[j][r][:4] for r in range(3)], "samples": samples.get(joint, [])[:6]}


def clip_track(name, joint):
    """The joint's height at every frame of a cooked clip."""
    c = anim.parse((A / f"cooked/clip_{name}.bin").read_bytes())
    dp = anim.parse_default_pose(hraw)
    j = names.index(joint)
    return [D.pose_of_clip(hier, anim.sample_frame(c, min(float(f), c.frame_count - 1 + 0.999), dp))[j][1][3] for f in range(c.frame_count + 1)]


expect["attack"] = {"state": GRAPH["states"]["attack_01"]["id"], "handHighest": max(clip_track("attack", "hand_r"))}
expect["dead"] = {"state": GRAPH["states"]["dead"]["id"], "pelvisEnd": clip_track("dead", "pelvis")[-1]}
(A / "cooked/expect.json").write_text(json.dumps(expect, indent=1))
print("ok", len(man["entries"]), "entries")
