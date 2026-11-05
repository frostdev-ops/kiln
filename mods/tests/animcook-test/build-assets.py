#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Builds the lab test's assets: our own glTF (no game data) -> kiln-style cook -> template copies and sequence overrides read from the installed
game (written under assets/cooked, gitignored, never committed).

    python3 mods/tests/animcook-test/build-assets.py
"""
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent.parent
sys.path[:0] = [str(ROOT / "tools/kfc"), str(ROOT / "tools/animcook"), str(ROOT / "tools/modelcook")]
A = HERE / "assets"
(A / "src").mkdir(parents=True, exist_ok=True)
subprocess.check_call([sys.executable, str(ROOT / "tools/animcook/samples/make_golem.py"), str(A / "src/golem.glb")])
subprocess.check_call([sys.executable, str(ROOT / "tools/animcook/animcook.py"), "recipe", str(A / "cook.json"), "--update-manifest"])

from kfc.archive import Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402
from anicook.graphgen import derive  # noqa: E402
from kfc.animgraph import fnv_guid  # noqa: E402

GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
k, t = Kfc(GAME), TypeDb.from_exe(GAME / "enshrouded.exe")
dec, enc = Decoder(t, max_array=1 << 26), Encoder(t)
MOD = "animcook-test"
entries = []


def emit(name, type_name, guid, value, mode):
    raw = enc.encode(t.get(type_name), value)
    (A / "cooked" / f"{name}.bin").write_bytes(raw)
    entries.append({"kind": "resource", "guid": guid, "type": type_name, "part": 0, "file": f"cooked/{name}.bin", "mode": mode})
    print(name, guid, len(raw))


TEMPLATE = t.get("keen::ecs::TemplateResource")
VUKAH = "d5d05217-52dc-467a-9192-26b4f661d8d9"
ANIM = json.loads((A / "cooked/golem.graph.json").read_text())
NEST = json.loads((A / "cooked/nested.graph.json").read_text())
PUPPET = ["CurrentTransform", "Actor", "ActorInput", "NetworkActor", "AnimationGraphResourceComponent2", "ServerAnimationGraphInput", "NetworkAnimationGraphInput",
          "ServerAnimationGraphComponent2", "ServerAnimationGraphSlotComponent", "Velocity", "LastTransform", "Faction"]


def template(name, graph, keep=None):
    v = dec.decode(TEMPLATE, k.read(ResourceKey(VUKAH, TEMPLATE["hash"], 0)))
    v["name"] = name
    out = []
    for c in v["components"]:
        nm = c["$type"].split("::")[-1]
        if nm in ("SubEntities", "HookShotAnchorSubEntity"):
            continue
        if keep is not None and nm not in keep:
            continue
        if nm == "AnimationGraphResourceComponent2":
            c["value"]["animationGraph"] = graph
        out.append(c)
    v["components"] = out
    emit("t_" + name, "keen::ecs::TemplateResource", derive(MOD, "template", name), v, "new")


template("Kiln_AnimCook_Puppet", ANIM["guid"], PUPPET)
template("Kiln_AnimCook_Nested", NEST["guid"], PUPPET)
template("Kiln_AnimCook_Golem", ANIM["guid"])

# Sequence overrides of small unused vanilla sequences: the only events are ours. A new sequence GUID would need the registry insert kiln.content does.
SEQ = t.get("keen::actor::ActorSequenceResource")
ZERO_TAGS = [{"tag": "00000000-0000-0000-0000-000000000000"}] * 4


def seq_event(type_name, **fields):
    et = t.get(type_name)
    v = dec.decode(et, b"\0" * et["size"])
    v.update(fields)
    return {"$type": type_name, "value": v}


BASE = {"lean1": "19cde0b8-3b8b-4641-8cd2-d59902bb683e"}  # Pet_Dog_eating
small = sorted((k.size_of(x), x.guid) for x in k.resources_of_type(SEQ["hash"]))
pick = [g for _, g in small if g not in BASE.values()]
names = ["lean2", "overlay", "slam", "step"]
for nm, g in zip(names, pick[:40]):
    BASE[nm] = g
STATES = ANIM["states"]
LEAN = derive(MOD, "input", "lean")


def override(key, events, length):
    g = BASE[key]
    v = dec.decode(SEQ, k.read(ResourceKey(g, SEQ["hash"], 0)))
    ss = v["subSequences"][0]
    v["subSequences"] = [ss]
    ss["events"] = events
    ss["length"] = {"value": int(length * 1e9)}
    ss["triggerRequirements"] = {"requiredActorStates": [], "forbiddenActorStates": []}
    emit("seq_" + key, "keen::actor::ActorSequenceResource", g, v, "override")


def set_state(state):
    return seq_event("keen::actor::SetAnimationEvent", animationName=STATES[state]["guid"], alignLveLocomotionToFloor=True, retriggerAnimation=True)


DUR2 = {"value": 2000000000}  # the request lives as long as the event lasts
override("lean1", [set_state("lean"), seq_event("keen::actor::SetAnimationFloatInputEvent", input=LEAN, value=1.0, duration=DUR2)], 2.0)
override("lean2", [set_state("lean"), seq_event("keen::actor::SetAnimationFloatInputEvent", input=LEAN, value=2.0, duration=DUR2)], 2.0)
override("overlay", [seq_event("keen::actor::SetAnimationOverlayEvent", overlayName="a6a6ee90-df38-40ee-8e58-fd144e6aef84", retriggerAnimation=True)], 1.0)
override("slam", [set_state("slam")], 1.5)
override("step", [set_state("step")], 2.0)

man = json.loads((A / "assets.json").read_text())
keys = {e["file"] for e in entries}
man["entries"] = [e for e in man["entries"] if e["file"] not in keys] + entries
(A / "assets.json").write_text(json.dumps(man, indent=2) + "\n")
# a control for root motion: the Vukah's own Attack_01 clip has one (state 0x2f9a458c)
control = None
for x in k.resources_of_type(SEQ["hash"]):
    v = dec.decode(SEQ, k.read(x))
    for ss in v["subSequences"]:
        for e in ss["events"]:
            if e and e["$type"].endswith("SetAnimationEvent") and fnv_guid(e["value"]["animationName"]) == 0x2F9A458C:
                control = x.guid
                break
    if control:
        break
BASE["control"] = control
expect = {"states": STATES, "nested": NEST["states"], "seq": BASE, "skeleton": json.loads((A / "cooked/golem.skeleton.json").read_text())}
(A / "cooked/expect.json").write_text(json.dumps(expect, indent=1))
print("ok", len(man["entries"]), "entries")
