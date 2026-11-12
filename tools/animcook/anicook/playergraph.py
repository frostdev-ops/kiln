# SPDX-License-Identifier: MIT
"""Mods' player states merged into the player's animation graph, one override for every loaded mod. The reference for the native
merge, which is compared with this one byte for byte: every step below is written in the order a port follows, and nothing in it
depends on the order mods or files are given.

    merge(raw, types, mods, clip_facts) -> (merged graph bytes or None, report)
    ClipFacts(types, mod_asset_dirs, kfc)  the facts of a clip from the mods' assets/assets.json, else from the installed game

What the merge does for a new state N whose neighbour (`like`) is L in machine M, in this order:
 1. clip: a copy of L's clip node (the first SampleAnimationClip under L's pose, depth first, POSE_INPUTS in field order) with the
    clip's GUID and hash, animationDuration = frameCount / fps (float32), root motion and hasSequences from the clip's resources,
    every gender alternative cleared, loop from the definition or the clip's flag, the additive reference = the clip itself when the
    node is not additive, the raw speed multiplier from the definition. Every other field stays L's.
 2. pose: a PoseResult over the clip; when L's PoseResult reads a LayerBlend over a UseCachedPose, over a copy of that LayerBlend
    with the clip as its layer.
 3. state: a copy of L's state node: its own id, debugName "<mod>.<name>", the new pose, retriggerBlendDuration = blendIn if given.
 4. entry: one global transition of M to N, a copy of L's first (or, L having none, duration and modes of the first local
    transition into L on an id), with duration = blendIn if given and a new condition on N's own id.
 5. local transitions: M's transitions out of L copied out of N with the same condition; those into L on L's id copied into N on a
    new condition on N's id.
 6. N's id appended to every list on the state input in the graph that holds all of L's entry ids, except L's own entry condition;
    then to usedInputIds.
 7. a SelectPoseByParameterId on the state input outside L's own pose that holds one of L's entry ids takes N's id with that pose.
 8. more than MAX_GLOBAL global transitions in M: all of M's new states go into nested machines of at most MAX_GLOBAL states, each
    behind a wrapper state of M entered on the list of its states' ids. Nested states get rule 4 in their machine and no local
    transitions. Groups beyond M's free global transitions are refused.
 9. new nodes go just before M, in the order made: per state clip, layer, pose, state, condition, then the transitions out of N, then
    for each transition into N its condition and itself; a nested machine after its states, then its pose, wrapper and list.
10. node ids: FNV-1a-32 of "kiln.player.<mod>.<state>.<role>", nested machines "kiln.player.nest.<machine>.<group>.<role>".
11. machines in the order of their nodes in the graph, in a machine the states by mod id, then name, as UTF-8 bytes.
12. no state accepted: "untouched", no graph.
A neighbour has a kind (SUPPORTED): "state" (entered when the state input holds its id) or "overlay" (entered when an overlay is
active, IsOverlayActive). For an overlay neighbour the conditions of rules 4 to 6 are IsOverlayActive and IsOverlayActiveList on N's id
(the same FNV-1a of its GUID), rule 7 does not apply, nothing goes into usedInputIds, and rule 8 does not nest: states past the machine's
free global transitions are refused. Overlay states that cannot be neighbours (OverlayEntry.refusal): the rest state, a state holding a
nested machine, a state with no overlay condition of its own, a state entered on a list of overlays.
Every new node is a copy of a node of the game's graph with named fields changed, so a field the game adds is carried: the clip, layer
blend, PoseResult and state node are copies of the neighbour's, a condition is a copy of the graph's first node of its type, a nested
machine a copy of M, its wrapper a copy of the group's first neighbour's state node.
Then the result is checked (validate()) and any failure turns the whole merge off: no graph, every state reported unavailable.
"""
import copy
import hashlib
import struct

from kfc.animgraph import MAX_GLOBAL, NODE, STATE_INPUT, ZERO, fnv_guid
from kfc.archive import ResourceKey
from kfc.blob import Decoder, DecodeError
from kfc.blobwrite import Encoder, EncodeError
from kfc.util import fnv1a32

from . import playerstates as PS

PLAYER_GRAPH = "21319e83-5087-4fff-8795-22997843649e"  # the player's AnimationGraphResource2_0: the resource this merge reads and serves
GRAPH_TYPE = NODE + "AnimationGraphResource2_0"
GT_TYPE = NODE + "StateMachineGlobalTransitionInfo"
JOINT_ANIM, ROOT_MOTION, SEQUENCES = "keen::JointAnimation", "keen::RootMotionAnimation", "keen::AnimationSequenceContainer"
MAX_ARRAY = 1 << 26

# The neighbours a mod's state may be like, with the kind of entry they take: every state of a machine (None), or in SM_Main the
# states named. One table, and the only names of the game in this code.
SUPPORTED = {
    "SM_Attack_Unarmed_FullBody": ("state", None),
    "SM_Attack_Sword_Shield_FullBody": ("state", None),
    "SM_Attack_Hammer_FullBody": ("state", None),
    "SM_Attack_Greatsword_FullBody": ("state", None),
    "SM_Attack_Dual_Daggers_FullBody": ("state", None),
    "SM_Jump_Attack_FullBody": ("state", None),
    "SM_Main": ("state", ("Block_FullBody", "Parry_FullBody", "DodgeRoll_FullBody", "Dodge_Attack_FullBody", "Sneak_Attack_FullBody",
                          "Attack_Skill_Kick_FullBody")),
    "SM_Attack_Staff_FullBody": ("state", None),
    "SM_Attack_Wand_FullBody": ("state", None),
    # overlay machines: every state with a global transition on an IsOverlayActive of its own (OverlayEntry.refusal says which are not)
    "SM_Staff_Overlay": ("overlay", None),
    "SM_Wand_Overlay": ("overlay", None),
    "SM_Ground_Bow_Overlay": ("overlay", None),
    "SM_Air_Bow_Overlay": ("overlay", None),
    "SM_Ranged_Attack_Additive": ("overlay", None),
}
HINTS = {
    "Idle_Block": "the legs while blocking, chosen by movement (a float input), never by a state id; a block pose of your own is "
                  "\"like\": \"Block_FullBody\", which keeps the legs and plays your clip as the guard",
    "Move_Block": "the legs while blocking, chosen by movement (a float input), never by a state id; a block pose of your own is "
                  "\"like\": \"Block_FullBody\", which keeps the legs and plays your clip as the guard",
}

# Rule 1: the inputs a walk from a state's pose follows, in field order. Any other node kind ends the walk: a UseCachedPose is a pose
# cached elsewhere, a StateMachine has states of its own, a BlendSpace_1D names its clips by GUID.
POSE_INPUTS = {"PoseResult": ("input",), "LayerBlend": ("base", "layer"), "SelectPoseByBool": ("inputA", "inputB"),
               "SelectPoseByParameterId": ("inputPoses[]",)}
# The fields of each node kind that hold a node id. Every non-zero value of these in the game's graph resolves to a node and points
# back in nodeDefinitions, except the two parent links in PARENT_REFS; no other field's values do (checked on the game's graph).
REFS = {
    "AnimationStateProgression": ("nodeStateHash",),
    "BipedFeetIKSolver": ("inputPose", "disableIK", "allowAdvancedFootPosition"),
    "BlendSpace_1D": ("items[].inputId", "parameterPinInputHash", "speedController.rawValueController.inputHash"),
    "BoolAnd": ("lhs", "rhs"),
    "BoolNegate": ("input",),
    "BoolOr": ("lhs", "rhs"),
    "BoolOrMultiple": ("entries[]",),
    "ConstraintOrientation": ("inputPose", "constraintAxisWS"),
    "Container": ("poseResult",),
    "ContainerInput": ("inputPose",),
    "FloatEqualsConstant": ("input",),
    "FloatGreaterThanConstant": ("input",),
    "FloatLessThanConstant": ("input",),
    "FloatMultiply": ("lhs", "rhs"),
    "LayerBlend": ("base", "layer"),
    "OffsetBone": ("inputPose", "offset"),
    "PoseResult": ("input",),
    "RelevantAnimTimeRemainingRatio": ("nodeStateHash",),
    "RetargetSolver": ("inputPose", "applyLeftHandIk", "applyRightHandIk"),
    "SampleAnimationClip": ("progressionPin", "speedController.rawValueController.inputHash"),
    "SaveCachedFloatValue": ("inputValueId",),
    "SaveCachedPose": ("inputPose",),
    "SelectPoseByBool": ("inputA", "inputB", "selectA"),
    "SelectPoseByParameterId": ("inputPoses[]",),
    "StateMachine": ("states[]", "transitions[]", "entryState", "parentStateMachineStateId", "globalTransitions[].toState",
                     "globalTransitions[].conditionNode"),
    "StateMachineState": ("poseResult", "parentStateMachineId"),
    "StateMachineTransition": ("fromState", "toState", "conditionNode"),
    "UseCachedFloatValue": ("cachedFloatValueId",),
    "UseCachedPose": ("cachedPose",),
}
PARENT_REFS = {("StateMachineState", "parentStateMachineId"), ("StateMachine", "parentStateMachineStateId")}
HEADER_REFS = ("rootNode", "postProcessRootNode", "bipedFeetIkNodeId", "genericFeetIkNodeId")
GENDER_GUIDS = ("animationGenderAlternative", "rootMotionAnimationGenderAlternative", "additiveReferenceAnimationGenderAlternative")
GENDER_HASHES = tuple(f + "Hash" for f in GENDER_GUIDS)
STATE_FIELDS = ("id", "type", "debugName", "poseResult", "parentStateMachineId", "retriggerBlendDuration", "restartOnRetrigger")
MACHINE_FIELDS = ("id", "type", "states", "transitions", "transitionRefInfo", "entryState", "parentStateMachineStateId",
                  "globalTransitions", "numGlobalTransitions", "globalTransitionNodeId", "debugName")
GT_FIELDS = ("toState", "duration", "rootMotionMode", "eventMode", "conditionNode", "blendCurve", "blendProfile")
GT_FROM_LOCAL = ("duration", "rootMotionMode", "eventMode", "blendCurve", "blendProfile")  # rule 4 without a global transition
# Every field the merge reads or writes, by type: checked against the reflection before anything is merged, so a field the game
# renamed stops the merge instead of vanishing from the copy.
USES = {
    GRAPH_TYPE: ("usedInputIds", "nodeDefinitions", "syncNodes") + HEADER_REFS,
    NODE + "ParameterList": ("parameterIds",),
    NODE + "SyncNodesItem_Runtime": ("node1", "node2"),
    NODE + "SampleAnimationClipNodeDefinition": ("id", "type", "loop", "animationDuration", "animation", "animationHash",
                                                 "rootMotionAnimation", "rootMotionAnimationHash", "additiveType",
                                                 "additiveReferenceAnimation", "additiveReferenceAnimationHash", "hasSequences",
                                                 "speedController") + GENDER_GUIDS + GENDER_HASHES,
    "keen::anim_graph::AnimationSpeedController": ("rawValueController",),
    "keen::anim_graph::AnimationSpeedControllerRawValue": ("speedMultiplier",),
    NODE + "PoseResultNodeDefinition": ("id", "type", "input"),
    NODE + "LayerBlendNodeDefinition": ("id", "type", "base", "layer", "blendOptions"),
    NODE + "UseCachedPoseNodeDefinition": ("id", "type"),
    NODE + "StateMachineStateNodeDefinition": STATE_FIELDS,
    NODE + "StateMachineNodeDefinition": MACHINE_FIELDS,
    NODE + "TransitionRefInfo": ("fromState", "toState", "duration"),
    GT_TYPE: GT_FIELDS,
    NODE + "StateMachineTransitionNodeDefinition": ("id", "type", "fromState", "toState", "conditionNode") + GT_FROM_LOCAL,
    NODE + "IdParameterEqualsConstantNodeDefinition": ("id", "type", "parameterHash", "parameterId", "value", "ignoreForAnimationSequences"),
    NODE + "IdParameterEqualsConstantListNodeDefinition": ("id", "type", "parameterHash", "parameterId", "values",
                                                           "ignoreForAnimationSequences"),
    NODE + "SelectPoseByParameterIdNodeDefinition": ("id", "type", "parameterHash", "inputPoses", "values"),
    NODE + "IsOverlayActiveNodeDefinition": ("id", "type", "overlay"),
    NODE + "IsOverlayActiveListNodeDefinition": ("id", "type", "overlays"),
}


class MergeOff(Exception):
    """The merge cannot be proven: nothing is served."""


def node_id(*parts):
    """Rule 10: FNV-1a-32 of "kiln.player.<parts joined by dots>"."""
    return fnv1a32(".".join(("kiln", "player") + tuple(str(p) for p in parts)).encode())


def full_type(kind):
    return NODE + kind + "NodeDefinition"


def _h(x):
    return f"0x{x:08x}"


class StateEntry:
    """Kind "state": a state is entered when the state input holds its id. Its conditions are IdParameterEqualsConstant and
    IdParameterEqualsConstantList on STATE_INPUT, and SelectPoseByParameterId nodes on STATE_INPUT pick poses by it. A second kind
    (an overlay machine: IsOverlayActive and IsOverlayActiveList) is a second class with the same members."""
    kind = "state"
    single, multi, values = "IdParameterEqualsConstant", "IdParameterEqualsConstantList", "values"
    selects = True  # rule 7
    used_ids = True  # the id goes into usedInputIds
    nests = True  # rule 8: past 34, nested machines

    def refusal(self, g, state, machine, gts):
        """Why a state of a supported machine cannot be a neighbour of this kind, before rules 1 to 7 look at it; None: it can."""
        return None

    def ids(self, node):
        """The ids a condition node compares on this kind's input, or None when it is no such compare."""
        if node is None or node.get("parameterHash", {}).get("value") != STATE_INPUT:
            return None
        if node["type"] == self.single:
            return [node["value"]["value"]]
        if node["type"] == self.multi:
            return [x["value"] for x in node["values"]]
        return None

    def is_list(self, node):
        return node["type"] == self.multi and node["parameterHash"]["value"] == STATE_INPUT

    def new(self, template, nid, value):
        """A condition on `value`: a copy of `template` (the graph's first node of the single type), so a field the game adds is carried.
        ignoreForAnimationSequences is set false as well: a sequence has to be able to enter the state."""
        w = copy.deepcopy(template)
        c = w["value"]
        c["id"], c["parameterHash"], c["parameterId"], c["value"] = {"value": nid}, {"value": STATE_INPUT}, {"value": STATE_INPUT}, {"value": value}
        c["ignoreForAnimationSequences"] = False
        return w

    def new_list(self, template, nid, values):
        w = copy.deepcopy(template)
        c = w["value"]
        c["id"], c["parameterHash"], c["parameterId"] = {"value": nid}, {"value": STATE_INPUT}, {"value": STATE_INPUT}
        c["values"] = [{"value": v} for v in values]
        c["ignoreForAnimationSequences"] = False
        return w


class OverlayEntry:
    """Kind "overlay": a state of an overlay machine is entered when an overlay is active (a sequence's SetAnimationOverlayEvent.overlayName,
    a GUID, whose FNV-1a-32 is the overlay id). Conditions: IsOverlayActive and IsOverlayActiveList. No select picks a pose by an
    overlay, overlay ids are not in usedInputIds (0 of 35 in the game's graph), and an overlay machine does not nest: a nested state could
    not go back to the machine's rest state, local transitions do not cross machines."""
    kind = "overlay"
    single, multi, values = "IsOverlayActive", "IsOverlayActiveList", "overlays"
    selects = False
    used_ids = False
    nests = False

    def ids(self, node):
        if node is None:
            return None
        if node["type"] == self.single:
            return [node["overlay"]["value"]]
        if node["type"] == self.multi:
            return [x["value"] for x in node["overlays"]]
        return None

    def is_list(self, node):
        return node["type"] == self.multi

    def refusal(self, g, state, machine, gts):
        pose = g.by_id.get(state["poseResult"]["value"])
        top = g.by_id.get(pose["input"]["value"]) if pose and pose["type"] == "PoseResult" else None
        if top is not None and top["type"] == "StateMachine":
            return f"it holds the nested machine {top['debugName']}: a state can be like one of that machine's states"
        if not gts:
            if machine["entryState"]["value"] == state["id"]["value"]:
                return f"it is the rest state of {machine['debugName']}: no overlay enters it, the overlay states go back to it when they end"
            return "it has no overlay condition of its own: it is entered only from other states, by local transitions"
        if not any((g.by_id.get(e["conditionNode"]["value"]) or {}).get("type") == self.single for e in gts):
            return "it is entered on a list of overlays (IsOverlayActiveList), not on an overlay of its own"
        return None

    def new(self, template, nid, value):
        """An IsOverlayActive on overlay `value`: a copy of the graph's first IsOverlayActive with id and overlay set."""
        w = copy.deepcopy(template)
        w["value"]["id"], w["value"]["overlay"] = {"value": nid}, {"value": value}
        return w

    def new_list(self, template, nid, values):
        w = copy.deepcopy(template)
        w["value"]["id"], w["value"]["overlays"] = {"value": nid}, [{"value": v} for v in values]
        return w


ENTRIES = {"state": StateEntry(), "overlay": OverlayEntry()}


# ---- the graph, indexed ----

def _get_path(value, path):
    """The {"value": id} leaves at `path` ("a.b", "a[].b", "a[]")."""
    cur = [value]
    for seg in path.split("."):
        arr = seg.endswith("[]")
        name = seg[:-2] if arr else seg
        nxt = []
        for c in cur:
            x = c.get(name) if isinstance(c, dict) else None
            if x is None:
                continue
            nxt.extend(x if arr else [x])
        cur = nxt
    return [c["value"] for c in cur if isinstance(c, dict) and "value" in c]


class Graph:
    def __init__(self, value):
        self.value = value
        self.wrapped = value["nodeDefinitions"]
        self.nodes = [n["value"] for n in self.wrapped]
        self.by_id, self.pos = {}, {}
        for i, v in enumerate(self.nodes):
            nid = v["id"]["value"]
            if nid in self.by_id:
                raise MergeOff(f"the game's graph has node id {_h(nid)} twice")
            self.by_id[nid], self.pos[nid] = v, i
        self.machines = [v for v in self.nodes if v["type"] == "StateMachine"]
        self.first = {}  # node kind -> the first node of that kind (wrapped): the template new nodes of the kind are copied from
        for w in self.wrapped:
            self.first.setdefault(w["value"]["type"], w)
        self.ids = set(self.by_id) | {m["globalTransitionNodeId"]["value"] for m in self.machines}
        self.transitions = [v for v in self.nodes if v["type"] == "StateMachineTransition"]
        compared = {x["value"] for x in value["usedInputIds"]["parameterIds"]}
        for v in self.nodes:
            t = v["type"]
            if t in ("IdParameterEqualsConstant",):
                compared.add(v["value"]["value"])
            elif t in ("IdParameterEqualsConstantList", "SelectPoseByParameterId"):
                compared.update(x["value"] for x in v["values"])
            elif t == "IsOverlayActive":
                compared.add(v["overlay"]["value"])
            elif t == "IsOverlayActiveList":
                compared.update(x["value"] for x in v["overlays"])
        self.compared = compared

    def machine_of(self, state):
        return self.by_id.get(state["parentStateMachineId"]["value"])

    def states_named(self, name, machine=None):
        out = []
        for v in self.nodes:
            if v["type"] != "StateMachineState" or v["debugName"] != name:
                continue
            m = self.machine_of(v)
            if m is None or m["type"] != "StateMachine":
                continue
            if machine is None or m["debugName"] == machine:
                out.append((v, m))
        return out

    def machine_transitions(self, m):
        return [self.by_id[t["value"]] for t in m["transitions"]]

    def walk(self, pose_id, stop_at_clip):
        """Node ids reached from `pose_id` through POSE_INPUTS, depth first in field order. With stop_at_clip: the first clip's id."""
        seen, order = set(), []

        def go(nid):
            if nid in seen or nid not in self.by_id:
                return None
            seen.add(nid)
            order.append(nid)
            v = self.by_id[nid]
            if v["type"] == "SampleAnimationClip" and stop_at_clip:
                return nid
            for path in POSE_INPUTS.get(v["type"], ()):
                for r in _get_path(v, path):
                    hit = go(r)
                    if hit is not None:
                        return hit
            return None
        hit = go(pose_id)
        return hit if stop_at_clip else order


class Neighbour:
    """What rules 1 to 7 take from a neighbour L in machine M (read from the game's graph as decoded, never from merged nodes)."""

    def __init__(self, g, state, machine, kind):
        self.state, self.machine, self.kind = state, machine, kind
        self.error = None
        cond = ENTRIES[kind]
        sid = state["id"]["value"]
        gts = [e for e in machine["globalTransitions"][:machine["numGlobalTransitions"]] if e["toState"]["value"] == sid]
        self.error = cond.refusal(g, state, machine, gts)
        if self.error:
            return
        # rules 1, 2
        pose = g.by_id.get(state["poseResult"]["value"])
        if pose is None or pose["type"] != "PoseResult":
            self.error = "its pose is not a PoseResult"
            return
        self.pose = pose
        top = g.by_id.get(pose["input"]["value"])
        clip = g.walk(pose["id"]["value"], stop_at_clip=True)
        if clip is None:
            self.error = "no SampleAnimationClip under its pose (its clip is not played by a clip node)"
            return
        self.clip = g.by_id[clip]
        self.layer = None
        if top is not None and top["type"] == "LayerBlend":
            base = g.by_id.get(top["base"]["value"])
            if base is None or base["type"] != "UseCachedPose":
                self.error = f"its pose is a LayerBlend over a {base['type'] if base else 'missing node'}, not over a cached pose"
                return
            self.layer = top
        # rule 4
        trans = g.machine_transitions(machine)
        if gts:
            self.entry = gts[0]
            self.entry_local = None
            ids = [i for e in gts for i in (cond.ids(g.by_id.get(e["conditionNode"]["value"])) or [])]
        else:
            ins = [t for t in trans if t["toState"]["value"] == sid and cond.ids(g.by_id.get(t["conditionNode"]["value"]))]
            if not ins:
                self.error = "it has no global transition and no local transition into it on an id"
                return
            self.entry, self.entry_local = None, ins[0]
            ids = [i for t in ins for i in cond.ids(g.by_id[t["conditionNode"]["value"]])]
        self.entry_ids = list(dict.fromkeys(ids))
        if not self.entry_ids:
            self.error = f"its entry condition does not compare the {kind} input"
            return
        entry_set = set(self.entry_ids)
        own = {e["conditionNode"]["value"] for e in gts} | {t["conditionNode"]["value"] for t in g.transitions if t["toState"]["value"] == sid}
        # rule 5
        self.outs = [t for t in trans if t["fromState"]["value"] == sid]
        self.ins = []
        for t in trans:
            if t["toState"]["value"] != sid:
                continue
            c = cond.ids(g.by_id.get(t["conditionNode"]["value"]))
            if c and set(c) <= entry_set:
                self.ins.append(t)
        # rule 6
        self.lists = [v for v in g.nodes if cond.is_list(v) and v["id"]["value"] not in own and entry_set <= {x["value"] for x in v[cond.values]}]
        # rule 7
        self.selects = []
        if cond.selects:
            mine = set(g.walk(pose["id"]["value"], stop_at_clip=False))
            for v in g.nodes:
                if v["type"] != "SelectPoseByParameterId" or v["parameterHash"]["value"] != STATE_INPUT or v["id"]["value"] in mine:
                    continue
                at = next((i for i, x in enumerate(v["values"]) if x["value"] in entry_set), None)
                if at is not None:
                    self.selects.append((v, v["inputPoses"][at]["value"]))

    def roles(self):
        """The role names of a state beside this neighbour, as if it is not nested (a superset of a nested state's)."""
        r = ["clip"] + (["layer"] if self.layer is not None else []) + ["pose", "state", "cond"]
        r += [f"out.{i}" for i in range(len(self.outs))]
        for i in range(len(self.ins)):
            r += [f"in.{i}.cond", f"in.{i}"]
        return r

    def entry_fields(self):
        """Rule 4's transition to copy, as a global transition entry (toState and conditionNode still L's or 0)."""
        if self.entry is not None:
            return copy.deepcopy(self.entry)
        t = self.entry_local
        e = {"toState": {"value": 0}, "conditionNode": {"value": 0}}
        for f in GT_FROM_LOCAL:
            e[f] = copy.deepcopy(t[f])
        return {f: e[f] for f in GT_FIELDS}


def resolve_like(g, like):
    """-> ((state node, machine node), None) or (None, why)."""
    if "/" in like:
        mname, sname = like.split("/", 1)
        found = g.states_named(sname, mname)
    else:
        sname = like
        found = g.states_named(sname)
    if not found:
        hint = f" ({HINTS[sname]})" if sname in HINTS else ""
        return None, f"like {like!r}: no state of the player's graph has that name{hint}"
    if len(found) > 1:
        names = ", ".join(sorted(f"{m['debugName']}/{s['debugName']}" for s, m in found))
        return None, f"like {like!r}: several states have that name: {names}; write Machine/State"
    return found[0], None


def support(state, machine):
    """-> (kind, None) or (None, why)."""
    mname, sname = machine["debugName"], state["debugName"]
    row = SUPPORTED.get(mname)
    if row is not None and (row[1] is None or sname in row[1]):
        return row[0], None
    if sname in HINTS:
        return None, f"{mname}/{sname} is {HINTS[sname]}"
    allowed = ", ".join(m for m, r in SUPPORTED.items() if r[1] is None)
    main = "; ".join(f"in {m}: {', '.join(r[1])}" for m, r in SUPPORTED.items() if r[1] is not None)
    return None, f"{mname}/{sname} cannot be a neighbour yet: a state may be like any state of {allowed}; {main}"


# ---- checks ----

def check_uses(types):
    for tname, fields in USES.items():
        t = types.get(tname)
        if t is None:
            raise MergeOff(f"the game's reflection has no {tname}")
        have = {f["name"] for f in types.all_fields(t)}
        missing = [f for f in fields if f not in have]
        if missing:
            raise MergeOff(f"the game's {tname} has no field {', '.join(missing)} (renamed?): the merge would lose it")


def check_shape(types, t, v, where):
    """A value has exactly the fields of its type, all the way down (a renamed field in a copy must not vanish)."""
    from kfc import refl as R
    t = types.resolve(t)
    if t is None:
        return
    k = t["kind"]
    if k == R.K_STRUCT:
        want = [f["name"] for f in types.all_fields(t)]
        if not isinstance(v, dict) or set(v) != set(want):
            got = set(v) if isinstance(v, dict) else set()
            raise MergeOff(f"{where}: fields differ from {t['name']}'s: {sorted(set(want) ^ got)}")
        for f in types.all_fields(t):
            check_shape(types, types.get(f["type"]), v[f["name"]], f"{where}.{f['name']}")
    elif k in (R.K_STATIC_ARRAY, R.K_BLOB_ARRAY):
        if isinstance(v, dict) and "bytes" in v:
            return
        if not isinstance(v, list) or (k == R.K_STATIC_ARRAY and len(v) != t["count"]):
            raise MergeOff(f"{where}: not an array of {t['name']}")
        el = types.get(t["inner"])
        for i, x in enumerate(v):
            check_shape(types, el, x, f"{where}[{i}]")
    elif k == R.K_BLOB_VARIANT:
        if v is not None:
            check_shape(types, types.get(v["$type"]), v["value"], where)


def validate(types, pristine, merged, touched_machines, touched_lists, touched_selects, new_ids):
    """The checks of the merged graph. Raises MergeOff with the first failure."""
    nodes = [n["value"] for n in merged["nodeDefinitions"]]
    pos = {}
    for i, v in enumerate(nodes):
        nid = v["id"]["value"]
        if nid in pos:
            raise MergeOff(f"node id {_h(nid)} twice")
        pos[nid] = i
    gtn = set()
    for v in nodes:
        if v["type"] == "StateMachine":
            x = v["globalTransitionNodeId"]["value"]
            if x in pos or x in gtn:
                raise MergeOff(f"machine {v['debugName']}: globalTransitionNodeId {_h(x)} is not unique")
            gtn.add(x)
    for i, v in enumerate(nodes):
        for path in REFS.get(v["type"], ()):
            for r in _get_path(v, path):
                if r == 0:
                    continue
                if r not in pos:
                    raise MergeOff(f"{v['type']} {_h(v['id']['value'])}: {path} = {_h(r)} is no node")
                if pos[r] >= i and (v["type"], path) not in PARENT_REFS:
                    raise MergeOff(f"{v['type']} {_h(v['id']['value'])}: {path} points forward")
    for f in HEADER_REFS:
        r = merged[f]["value"]
        if r and r not in pos:
            raise MergeOff(f"{f} = {_h(r)} is no node")
    for s in merged["syncNodes"]:
        for f in ("node1", "node2"):
            if s[f]["value"] and s[f]["value"] not in pos:
                raise MergeOff(f"syncNodes: {f} = {_h(s[f]['value'])} is no node")
    gtt = types.get(GT_TYPE)
    blank = Decoder(types).decode(gtt, b"\0" * gtt["size"])
    for v in nodes:
        if v["type"] != "StateMachine":
            continue
        n = v["numGlobalTransitions"]
        if n > MAX_GLOBAL or len(v["globalTransitions"]) != MAX_GLOBAL:
            raise MergeOff(f"machine {v['debugName']}: {n} global transitions, the game holds {MAX_GLOBAL}")
        if any(e["toState"]["value"] == 0 for e in v["globalTransitions"][:n]) or any(e != blank for e in v["globalTransitions"][n:]):
            raise MergeOff(f"machine {v['debugName']}: numGlobalTransitions {n} does not match its entries")
    # the game's nodes: all there, in order, equal but for what took states and ids
    before = pristine["nodeDefinitions"]
    by_pos = [n for n in merged["nodeDefinitions"] if n["value"]["id"]["value"] not in new_ids]
    if len(by_pos) != len(before):
        raise MergeOff(f"{len(before)} nodes in the game's graph, {len(by_pos)} of them after the merge")
    lists = {e.multi: (e.values,) for e in ENTRIES.values()}  # the list kinds that may take ids, and their field
    grow = dict({"StateMachine": ("states", "transitions"), "SelectPoseByParameterId": ("values", "inputPoses")}, **lists)
    for a, b in zip(before, by_pos):
        va, vb = a["value"], b["value"]
        nid = va["id"]["value"]
        if a["$type"] != b["$type"] or nid != vb["id"]["value"]:
            raise MergeOff(f"node {_h(nid)} moved or changed its type")
        if va == vb:
            continue
        allowed = ((nid in touched_machines and va["type"] == "StateMachine") or (nid in touched_lists and va["type"] in lists)
                   or (nid in touched_selects and va["type"] == "SelectPoseByParameterId"))
        if not allowed:
            raise MergeOff(f"the game's {va['type']} {_h(nid)} changed")
        fields = grow[va["type"]] + (("globalTransitions", "numGlobalTransitions") if va["type"] == "StateMachine" else ())
        for f in va:
            if f not in fields and va[f] != vb[f]:
                raise MergeOff(f"the game's {va['type']} {_h(nid)}: {f} changed")
        for f in grow[va["type"]]:
            if vb[f][:len(va[f])] != va[f]:
                raise MergeOff(f"the game's {va['type']} {_h(nid)}: {f} lost or reordered entries")
        if va["type"] == "StateMachine":
            n = va["numGlobalTransitions"]
            if vb["globalTransitions"][:n] != va["globalTransitions"][:n] or vb["numGlobalTransitions"] < n:
                raise MergeOff(f"machine {va['debugName']}: its own global transitions changed")
    for f in pristine:
        if f in ("nodeDefinitions", "usedInputIds"):
            continue
        if pristine[f] != merged[f]:
            raise MergeOff(f"the graph's {f} changed")
    ua, ub = pristine["usedInputIds"]["parameterIds"], merged["usedInputIds"]["parameterIds"]
    if ub[:len(ua)] != ua:
        raise MergeOff("usedInputIds lost or reordered entries")
    for n in merged["nodeDefinitions"]:
        if n["value"]["id"]["value"] in new_ids or n["value"]["id"]["value"] in touched_machines | touched_lists | touched_selects:
            check_shape(types, types.get(n["$type"]), n["value"], f"{n['value']['type']} {_h(n['value']['id']['value'])}")
    check_shape(types, types.get(NODE + "ParameterList"), merged["usedInputIds"], "usedInputIds")


# ---- the merge ----

def graph_hash(data):
    """The report's graphHash (the runtime sends it at join): the first 8 bytes of SHA-256 over the merged graph, 16 hex digits."""
    return hashlib.sha256(data).digest()[:8].hex() if data is not None else "0" * 16


def _new_report():
    return {"format": 1, "graph": "untouched", "reason": None, "graphHash": "0" * 16, "states": [], "errors": [], "machines": [], "nodesAdded": 0,
            "lists": 0, "listEntries": 0, "selects": 0, "usedInputIds": 0, "bytes": {"before": None, "after": None}}


def _finish(report, states, graph, reason=None, raw=None, out=None):
    if graph != "merged":
        report.update({k: v for k, v in _new_report().items() if k in ("machines", "nodesAdded", "lists", "listEntries", "selects", "usedInputIds")})
    for st in states:
        if st.status is None:
            st.status, st.reason = ("live", None) if graph == "merged" else ("refused", f"mod {st.mod}, state {st.name}: the merge is off: {reason}")
    report["graph"], report["reason"] = graph, reason
    report["graphHash"] = graph_hash(out if graph == "merged" else None)
    report["states"] = [st.entry() for st in sorted(states, key=PS.State.key)]
    report["bytes"] = {"before": len(raw) if isinstance(raw, (bytes, bytearray)) else None, "after": len(out) if graph == "merged" else None}
    return (out if graph == "merged" else None), report


def merge(raw, types, mods, clip_facts):
    """`raw`: the game's graph bytes, or a function that returns them (called only when a state is left to merge). `mods`:
    playerstates.ModStates. `clip_facts(guid)` -> {hierarchyHash, jointCount, fps, frameCount, loop, rootMotion, sequences} or None.
    -> (merged bytes or None, report)."""
    report = _new_report()
    copies = [(m, [copy.copy(st) for st in m.states]) for m in mods]  # the definitions are not changed: they can be merged again
    states = [st for _, sts in copies for st in sts]
    by_mod = {}
    for m, sts in copies:
        by_mod.setdefault(m.mod, []).append((m, sts))
    for mod, ms in by_mod.items():
        if len(ms) > 1:
            where = ", ".join(sorted(str(m.source) for m, _ in ms))
            for _, sts in ms:
                for st in sts:
                    st.refuse(f"mod {mod} is given more than once ({where})")
    if not any(st.status is None for st in states):
        return _finish(report, states, "untouched", raw=raw)
    if callable(raw):
        try:
            raw = raw()
        except (OSError, KeyError, ValueError) as e:
            return _finish(report, states, "off", f"the game's graph cannot be read: {e}")
    if raw is None:
        return _finish(report, states, "off", "no graph to merge into")
    dec, enc = Decoder(types, max_array=MAX_ARRAY), Encoder(types)
    try:
        if types.get(GRAPH_TYPE) is None:
            raise MergeOff(f"the reflection has no {GRAPH_TYPE}")
        check_uses(types)
        value = dec.decode(GRAPH_TYPE, raw)
        if enc.encode(GRAPH_TYPE, value) != raw:
            raise MergeOff("the game's graph does not encode back to its own bytes with this reflection")
        pristine = dec.decode(GRAPH_TYPE, raw)
        g = Graph(value)
        accepted = _accept(g, states, clip_facts, report)
        if not accepted:
            return _finish(report, states, "untouched", raw=raw)
        out = _build(g, types, accepted, report)
        new_ids, tm, tl, ts = out
        validate(types, pristine, value, tm, tl, ts, new_ids)
        merged = enc.encode(GRAPH_TYPE, value)
        back = dec.decode(GRAPH_TYPE, merged)
        if back != value or enc.encode(GRAPH_TYPE, back) != merged:
            raise MergeOff("the merged graph does not decode to itself")
    except MergeOff as e:
        return _finish(report, states, "off", str(e), raw=raw)
    except (DecodeError, EncodeError, KeyError, TypeError, ValueError, IndexError, struct.error) as e:
        return _finish(report, states, "off", f"{type(e).__name__}: {e}", raw=raw)
    return _finish(report, states, "merged", raw=raw, out=merged)


def _accept(g, states, clip_facts, report):
    """Rules on each state alone, then between states, then per machine (rule 8). -> {machine id: (machine, [(state, neighbour)],
    nested groups or None)} of what is left."""
    neighbours = {}
    pending = []
    for st in sorted((s for s in states if s.status is None), key=PS.State.key):
        found, why = resolve_like(g, st.like)
        if why:
            st.refuse(why)
            continue
        L, M = found
        st.machine = M["debugName"]
        kind, why = support(L, M)
        if why:
            st.refuse(f"like {st.like!r}: {why}")
            continue
        st.kind = kind
        lid = L["id"]["value"]
        if lid not in neighbours:
            neighbours[lid] = Neighbour(g, L, M, kind)
        nb = neighbours[lid]
        if nb.error:
            st.refuse(f"like {st.like!r} ({M['debugName']}/{L['debugName']}) cannot be a neighbour: {nb.error}")
            continue
        if ENTRIES[kind].single not in g.first:
            st.refuse(f"the game's graph has no {ENTRIES[kind].single} node to copy as its condition")
            continue
        facts = clip_facts(st.clip)
        if facts is None:
            st.refuse(f"clip {st.clip_ref!r} ({st.clip}) is not served: no {JOINT_ANIM} under that GUID in the mods' assets or the game")
            continue
        if not facts.get("fps"):
            st.refuse(f"clip {st.clip_ref!r} ({st.clip}) has 0 frames a second")
            continue
        own = clip_facts(nb.clip["animation"])
        if own is None:
            st.refuse(f"the neighbour's clip {nb.clip['animation']} cannot be read, so the skeleton cannot be checked")
            continue
        if (facts["hierarchyHash"], facts["jointCount"]) != (own["hierarchyHash"], own["jointCount"]):
            st.refuse(f"clip {st.clip_ref!r} ({st.clip}) is made for another skeleton: hierarchyHash 0x{facts['hierarchyHash']:016x}, "
                      f"{facts['jointCount']} joints; the player's is 0x{own['hierarchyHash']:016x}, {own['jointCount']} joints")
            continue
        if st.id in g.compared:
            st.refuse(f"its state id 0x{st.id:08x} is an id the game's graph already compares: rename the state")
            continue
        roles = nb.roles()
        rids = [node_id(st.mod, st.name, r) for r in roles]
        if len(set(rids)) != len(rids) or any(r in g.ids for r in rids):
            st.refuse("one of its node ids is taken in the game's graph: rename the state")
            continue
        st.facts, st.rids = facts, dict(zip(roles, rids))
        pending.append((st, nb))
    # between states (rule 10): two of ours alike refuses all of them
    for what, key in (("state id", lambda st: [st.id]), ("node id", lambda st: list(st.rids.values()))):
        by = {}
        for st, _ in pending:
            for x in key(st):
                by.setdefault(x, []).append(st)
        for same in by.values():
            names = sorted({(s.mod.encode(), s.name.encode(), s.mod, s.name) for s in same})
            if len(names) < 2:
                continue
            for s in same:
                others = ", ".join(f"{m}/{n}" for _, _, m, n in names if (m, n) != (s.mod, s.name))
                s.refuse(f"its {what} is the same as {others}'s: rename one")
    pending = [(st, nb) for st, nb in pending if st.status is None]
    # rule 8, per machine in graph order
    taken = {r for st, _ in pending for r in st.rids.values()}
    plan = {}
    for st, nb in pending:
        plan.setdefault(nb.machine["id"]["value"], []).append((st, nb))
    out = {}
    for M in g.machines:
        mid = M["id"]["value"]
        if mid not in plan:
            continue
        items = plan[mid]
        num = M["numGlobalTransitions"]
        if num + len(items) <= MAX_GLOBAL:
            out[mid] = (M, items, None)
            continue
        free = MAX_GLOBAL - num
        if not ENTRIES[items[0][1].kind].nests:
            for st, _ in items[free:]:
                st.refuse(f"machine {M['debugName']} has room for {free} new states across all mods ({MAX_GLOBAL} global transitions, {num} its "
                          f"own) and an overlay machine does not nest: the states before this one in mod and name order take them")
            if free > 0:
                out[mid] = (M, items[:free], None)
            continue
        groups = [items[i:i + MAX_GLOBAL] for i in range(0, len(items), MAX_GLOBAL)]
        kept = []
        for gi, group in enumerate(groups):
            if gi >= free:
                for st, _ in group:
                    st.refuse(f"machine {M['debugName']} is full: {num} global transitions of its own, {free} left for nested machines of "
                              f"{MAX_GLOBAL} states each, and the states before this one in mod and name order fill them")
                continue
            multi = ENTRIES[group[0][1].kind].multi
            if multi not in g.first:
                for st, _ in group:
                    st.refuse(f"machine {M['debugName']} is full and the game's graph has no {multi} node to copy for a nested machine's entry")
                continue
            nids = [node_id("nest", M["debugName"], gi, r) for r in ("machine", "pose", "wrapper", "list", "global")]
            if len(set(nids)) != len(nids) or any(r in g.ids or r in taken for r in nids):
                for st, _ in group:
                    st.refuse(f"the node ids of nested machine {gi} of {M['debugName']} are taken in the game's graph")
                continue
            taken.update(nids)
            kept.append((gi, group, nids))
        if kept:
            out[mid] = (M, [x for _, grp, _ in kept for x in grp], kept)
    return out


def _clip_node(st, nb):
    w = {"$type": full_type("SampleAnimationClip"), "value": copy.deepcopy(nb.clip)}
    c = w["value"]
    f = st.facts
    h = fnv_guid(st.clip)
    c["id"] = {"value": st.rids["clip"]}
    c["animation"], c["animationHash"] = st.clip, {"value": h}
    c["animationDuration"] = PS.f32(f["frameCount"] / f["fps"])
    c["rootMotionAnimation"] = st.clip if f["rootMotion"] else ZERO
    c["rootMotionAnimationHash"] = {"value": h if f["rootMotion"] else 0}
    c["hasSequences"] = bool(f["sequences"])
    for x in GENDER_GUIDS:
        c[x] = ZERO
    for x in GENDER_HASHES:
        c[x] = {"value": 0}
    c["loop"] = st.loop if st.loop is not None else bool(f["loop"])
    if c["additiveType"] == "None":
        c["additiveReferenceAnimation"], c["additiveReferenceAnimationHash"] = st.clip, {"value": h}
    if st.speed is not None:
        c["speedController"]["rawValueController"]["speedMultiplier"] = st.speed
    return w


def _copy_of(kind, node, **fields):
    """A wrapped copy of a decoded node with the named fields replaced: every other field, including one the game adds later, is kept."""
    v = copy.deepcopy(node)
    v.update(fields)
    return {"$type": full_type(kind), "value": v}


def _state_nodes(g, st, nb, parent, local):
    """Rules 1 to 5 for one state. -> (nodes in rule 9's order, its global transition entry, its new transition ids)."""
    entry = ENTRIES[nb.kind]
    nodes = [_clip_node(st, nb)]
    top = st.rids["clip"]
    if nb.layer is not None:
        lb = copy.deepcopy(nb.layer)
        lb["id"], lb["layer"] = {"value": st.rids["layer"]}, {"value": st.rids["clip"]}
        nodes.append({"$type": full_type("LayerBlend"), "value": lb})
        top = st.rids["layer"]
    nodes.append(_copy_of("PoseResult", nb.pose, id={"value": st.rids["pose"]}, input={"value": top}))
    s = copy.deepcopy(nb.state)
    s["id"], s["debugName"], s["poseResult"] = {"value": st.rids["state"]}, f"{st.mod}.{st.name}", {"value": st.rids["pose"]}
    s["parentStateMachineId"] = {"value": parent}
    if st.blend_in is not None:
        s["retriggerBlendDuration"] = st.blend_in
    nodes.append({"$type": full_type("StateMachineState"), "value": s})
    nodes.append(entry.new(g.first[entry.single], st.rids["cond"], st.id))
    gt = nb.entry_fields()
    gt["toState"], gt["conditionNode"] = {"value": st.rids["state"]}, {"value": st.rids["cond"]}
    if st.blend_in is not None:
        gt["duration"] = st.blend_in
    trans = []
    if local:
        for i, t in enumerate(nb.outs):
            c = copy.deepcopy(t)
            c["id"], c["fromState"] = {"value": st.rids[f"out.{i}"]}, {"value": st.rids["state"]}
            nodes.append({"$type": full_type("StateMachineTransition"), "value": c})
            trans.append(st.rids[f"out.{i}"])
        for i, t in enumerate(nb.ins):
            nodes.append(entry.new(g.first[entry.single], st.rids[f"in.{i}.cond"], st.id))
            c = copy.deepcopy(t)
            c["id"], c["toState"], c["conditionNode"] = {"value": st.rids[f"in.{i}"]}, {"value": st.rids["state"]}, {"value": st.rids[f"in.{i}.cond"]}
            nodes.append({"$type": full_type("StateMachineTransition"), "value": c})
            trans.append(st.rids[f"in.{i}"])
    return nodes, gt, trans


def _build(g, types, plan, report):
    """Writes the accepted states into the graph (g.value). -> (new node ids, touched machines, lists, selects)."""
    gtt = types.get(GT_TYPE)
    blank = Decoder(types).decode(gtt, b"\0" * gtt["size"])
    blocks, new_ids = {}, set()
    touched_lists, touched_selects = {}, {}
    used = g.value["usedInputIds"]["parameterIds"]
    used_before = len(used)

    def take_ids(st, nb):
        entry = ENTRIES[nb.kind]
        for lst in nb.lists:
            lst[entry.values].append({"value": st.id})
            touched_lists[lst["id"]["value"]] = touched_lists.get(lst["id"]["value"], 0) + 1
        for sel, pose in nb.selects:
            sel["values"].append({"value": st.id})
            sel["inputPoses"].append({"value": pose})
            touched_selects[sel["id"]["value"]] = touched_selects.get(sel["id"]["value"], 0) + 1
        if entry.used_ids:
            used.append({"value": st.id})

    for M in g.machines:  # rule 11: machines in graph order
        mid = M["id"]["value"]
        if mid not in plan:
            continue
        _, items, groups = plan[mid]
        block, gts, new_states, new_trans = [], [], [], []
        if groups is None:
            for st, nb in items:
                nodes, gt, trans = _state_nodes(g, st, nb, mid, local=True)
                block += nodes
                gts.append(gt)
                new_states.append(st.rids["state"])
                new_trans += trans
                take_ids(st, nb)
        else:
            for gi, group, nids in groups:
                inner_id, pose_id, wrapper_id, list_id, global_id = nids
                inner_gts, inner_states = [], []
                for st, nb in group:
                    nodes, gt, _ = _state_nodes(g, st, nb, inner_id, local=False)
                    block += nodes
                    inner_gts.append(gt)
                    inner_states.append(st.rids["state"])
                    take_ids(st, nb)
                first_nb = group[0][1]
                name = f"kiln.nest.{M['debugName']}.{gi}"
                ref = copy.deepcopy(M["transitionRefInfo"])
                ref.update(fromState=[], toState=[], duration=[])
                inner = _copy_of("StateMachine", M, id={"value": inner_id}, states=[{"value": x} for x in inner_states], transitions=[],
                                 transitionRefInfo=ref, entryState={"value": inner_states[0]}, parentStateMachineStateId={"value": wrapper_id},
                                 globalTransitions=inner_gts + [copy.deepcopy(blank) for _ in range(MAX_GLOBAL - len(inner_gts))],
                                 numGlobalTransitions=len(inner_gts), globalTransitionNodeId={"value": global_id}, debugName=name)
                wgt = first_nb.entry_fields()
                wgt["toState"], wgt["conditionNode"] = {"value": wrapper_id}, {"value": list_id}
                wrapper = _copy_of("StateMachineState", first_nb.state, id={"value": wrapper_id}, debugName=name, poseResult={"value": pose_id},
                                   parentStateMachineId={"value": mid}, retriggerBlendDuration=wgt["duration"], restartOnRetrigger=False)
                entry = ENTRIES[first_nb.kind]
                block += [inner, _copy_of("PoseResult", first_nb.pose, id={"value": pose_id}, input={"value": inner_id}), wrapper,
                          entry.new_list(g.first[entry.multi], list_id, [st.id for st, _ in group])]
                gts.append(wgt)
                new_states.append(wrapper_id)
        num = M["numGlobalTransitions"]
        if num + len(gts) > MAX_GLOBAL:
            raise MergeOff(f"machine {M['debugName']}: {num + len(gts)} global transitions")
        M["states"] += [{"value": x} for x in new_states]
        M["transitions"] += [{"value": x} for x in new_trans]
        M["globalTransitions"][num:num + len(gts)] = gts
        M["numGlobalTransitions"] = num + len(gts)
        blocks[mid] = block
        new_ids.update(n["value"]["id"]["value"] for n in block)
        report["machines"].append({"machine": M["debugName"], "states": len(items), "nested": len(groups) if groups else 0,
                                   "globalTransitions": [num, num + len(gts)], "nodesAdded": len(block)})
    merged = []
    for n in g.wrapped:
        b = blocks.get(n["value"]["id"]["value"])
        if b is not None and n["value"]["type"] == "StateMachine":
            merged += b
        merged.append(n)
    g.value["nodeDefinitions"] = merged
    report["nodesAdded"] = len(new_ids)
    report["lists"], report["listEntries"] = len(touched_lists), sum(touched_lists.values())
    report["selects"] = len(touched_selects)
    report["usedInputIds"] = len(used) - used_before
    return new_ids, set(blocks), set(touched_lists), set(touched_selects)


# ---- clip facts ----

def _scalars(types, type_name, raw, names):
    """Scalar fields of a reflected struct at the start of `raw`, by name (no blob is decoded)."""
    from kfc import refl as R
    t = types.get(type_name)
    fmt = {R.K_U8: "<B", R.K_U16: "<H", R.K_U32: "<I", R.K_U64: "<Q", R.K_BOOL: "<?"}
    out = {}
    for f in types.all_fields(t):
        if f["name"] in names:
            ft = types.resolve(types.get(f["type"]))
            out[f["name"]] = struct.unpack_from(fmt[ft["kind"]], raw, f["offset"])[0]
    missing = [n for n in names if n not in out]
    if missing:
        raise MergeOff(f"the game's {type_name} has no field {', '.join(missing)}")
    return out


def _type_hash(types, name):
    if isinstance(name, str) and name.startswith("0x"):
        return int(name, 16)
    t = types.get(name)
    return t["hash"] if t else fnv1a32(name.encode())


class ClipFacts:
    """clip_facts(guid) for the merge: a JointAnimation a mod serves (shared assets/assets.json "resource" entries), else the installed
    game's. A one-sided clip, root motion or sequences entry refuses that GUID on both sides. Root motion and sequences: a RootMotionAnimation or an
    AnimationSequenceContainer under the GUID served by any of them. Two mods serving the same clip with other facts: None (which one
    the game would get is not a rule of the merge)."""

    def __init__(self, types, mod_asset_dirs=(), kfc=None):
        import json
        import re
        import uuid
        self.types, self.kfc = types, kfc
        self.hashes = {n: _type_hash(types, n) for n in (JOINT_ANIM, ROOT_MOTION, SEQUENCES)}
        self.served = {}  # (guid, type hash) -> [file paths]
        self.one_sided = set()  # do not sample a clip whose resources can differ between server and client
        for d in mod_asset_dirs:
            p = d / "assets.json"
            if not p.exists():
                continue
            doc = json.loads(re.sub(r"^\s*//.*$", "", p.read_text(encoding="utf-8"), flags=re.M))
            for e in doc.get("entries", []):
                if e.get("kind") != "resource" or int(e.get("part", 0)) != 0:
                    continue
                if not isinstance(e.get("guid"), str) or not isinstance(e.get("type"), str) or not isinstance(e.get("file"), str):
                    continue
                try:
                    guid = str(uuid.UUID(e["guid"]))
                except ValueError:
                    continue
                th = _type_hash(types, e["type"])
                if th in self.hashes.values():
                    if e.get("side") in ("client", "server"):
                        self.one_sided.add(guid)
                        continue
                    self.served.setdefault((guid, th), []).append(d / e["file"])
        self.cache = {}

    def _game_has(self, guid, th):
        return self.kfc is not None and self.kfc.has(ResourceKey(guid, th, 0))

    def _header(self, raw):
        v = _scalars(self.types, JOINT_ANIM, raw, ("hierarchyHash", "fps", "frameCount", "jointCount", "flags"))
        return {"hierarchyHash": v["hierarchyHash"], "jointCount": v["jointCount"], "fps": v["fps"], "frameCount": v["frameCount"],
                "loop": bool(v["flags"] & 1)}

    def __call__(self, guid):
        guid = guid.lower()
        if guid in self.one_sided:
            return None
        if guid in self.cache:
            return self.cache[guid]
        ja, rm, sq = self.hashes[JOINT_ANIM], self.hashes[ROOT_MOTION], self.hashes[SEQUENCES]
        files = self.served.get((guid, ja), [])
        facts = None
        if files:
            heads = []
            for f in files:
                try:
                    heads.append(self._header(f.read_bytes()))
                except (OSError, struct.error, MergeOff):
                    heads.append(None)
            if heads and all(h is not None and h == heads[0] for h in heads):
                facts = heads[0]
        elif self._game_has(guid, ja):
            facts = self._header(self.kfc.read(ResourceKey(guid, ja, 0)))
        if facts is not None:
            facts["rootMotion"] = bool(self.served.get((guid, rm))) or self._game_has(guid, rm)
            facts["sequences"] = bool(self.served.get((guid, sq))) or self._game_has(guid, sq)
        self.cache[guid] = facts
        return facts


def read_game_graph(kfc, types):
    """The player's graph bytes from the installed game."""
    return kfc.read(ResourceKey(PLAYER_GRAPH, types.get(GRAPH_TYPE)["hash"], 0))
