# SPDX-License-Identifier: MIT
"""Writers for keen::ModelHierarchyResource and keen::anim_graph::runtime_graph::AnimationGraphResource2_0, plus readers
that print a graph for humans. Values are the plain dicts tools/kfc's Encoder takes. The rules are in
docs/re/npc-internals.md; a skeleton, clips and a graph written this way run on the dedicated server.

    skeleton_value(guid, names, parents, locals_, ...)       ModelHierarchyResource value + its joint name hashes
    Nodes(prefix), graph_value(nodes, skeleton, root, ...)   every node kind tools/animcook uses, and the graph around them
    GraphBuilder(skeleton)                                   a simple state machine of clips
    describe_graph(value)                                    states, clips and transitions of a decoded graph
"""
from .util import fnv1a32, guid_bytes

ZERO = "00000000-0000-0000-0000-000000000000"
NODE = "keen::anim_graph::runtime_graph::"
STATE_INPUT = 0x5D2BFB0A  # the id input every graph compares states against; the engine writes it (0x778850)
IDLE_STATE_GUID = "2e701760-3fb1-4e08-b14a-4e012cac15d0"  # FNV 0x1490bed7: every creature's default state
MAX_GLOBAL = 34  # StateMachineNodeDefinition.globalTransitions is a StaticArray of 34


def fnv_guid(g):
    return fnv1a32(guid_bytes(g))


def _v3(p):
    return {"x": float(p[0]), "y": float(p[1]), "z": float(p[2])}


def _blocks(data):
    return [{"data": list(data[i:i + 16])} for i in range(0, len(data), 16)]


def _quat_mat(q):
    x, y, z, w = q
    return [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]


def _affine(t, q, s):
    r = _quat_mat(q)
    return [[r[i][0] * s[0], r[i][1] * s[1], r[i][2] * s[2], t[i]] for i in range(3)]


def _mul_affine(a, b):
    out = []
    for i in range(3):
        row = [sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)]
        row.append(sum(a[i][k] * b[k][3] for k in range(3)) + a[i][3])
        out.append(row)
    return out


def _inverse_affine(m):
    a = [r[:3] for r in m]
    det = (a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1]) - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0])
           + a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]))
    if abs(det) < 1e-12:
        raise ValueError("a joint's rest transform cannot be inverted (a zero scale?)")
    inv = [[(a[(j + 1) % 3][(i + 1) % 3] * a[(j + 2) % 3][(i + 2) % 3] - a[(j + 1) % 3][(i + 2) % 3] * a[(j + 2) % 3][(i + 1) % 3]) / det
            for j in range(3)] for i in range(3)]
    t = [m[i][3] for i in range(3)]
    return [inv[i] + [-sum(inv[i][k] * t[k] for k in range(3))] for i in range(3)]


def joint_name_hash(name):
    """ModelHierarchyResource.jointNames entry of a Kiln skeleton. The game's own hash is unknown (stripped names) and
    nothing in the game looks a joint up by it; only JointAnimation.hierarchyHash is made from these."""
    return fnv1a32(("kiln:" + name).encode())


def skeleton_value(guid, names, parents, locals_, rotations=None, scales=None):
    """A ModelHierarchyResource value. Rest pose per joint, parent-relative, game space (left-handed, y up):
    `locals_` positions, `rotations` quaternions xyzw (default identity), `scales` (default 1). `parents[i]` is 0xFFFF
    for a root and must come before its children. Returns (value, joint name hashes). Limit: 255 joints (u8 indices in
    skin streams, bone masks and IK chains)."""
    import struct
    n = len(names)
    if not 1 <= n <= 255:
        raise ValueError(f"{n} joints: a skeleton has 1..255 (skin, bone mask and IK indices are u8)")
    if len(set(names)) != n:
        raise ValueError("duplicate joint names")
    rotations = rotations or [(0.0, 0.0, 0.0, 1.0)] * n
    scales = scales or [(1.0, 1.0, 1.0)] * n
    aligned = (n + 3) & ~3
    hashes = [joint_name_hash(s) for s in names]
    glob = []
    for i in range(n):
        if parents[i] != 0xFFFF and parents[i] >= i:
            raise ValueError("a joint's parent must come first")
        loc = _affine(locals_[i], rotations[i], scales[i])
        glob.append(loc if parents[i] == 0xFFFF else _mul_affine(glob[parents[i]], loc))
    skin = []
    for m in glob:
        inv = _inverse_affine(m)
        # Matrix43: the columns x, y, z of the rotation part, then the translation (docs/re/npc-internals.md 1.2)
        skin.append({"rot": {"x": _v3([inv[r][0] for r in range(3)]), "y": _v3([inv[r][1] for r in range(3)]),
                             "z": _v3([inv[r][2] for r in range(3)])},
                     "pos": _v3([inv[r][3] for r in range(3)])})

    def at(lst, i, pad):
        return tuple(lst[i]) if i < n else pad

    pose = b"".join(struct.pack("<4f", *at(rotations, i, (0, 0, 0, 1))) for i in range(aligned))
    pose += b"".join(struct.pack("<4f", *at(locals_, i, (0, 0, 0)), 0) for i in range(aligned))
    pose += b"".join(struct.pack("<4f", *at(scales, i, (1, 1, 1)), 1) for i in range(aligned))
    joints = []
    for i in range(aligned):
        q = at(rotations, i, (0.0, 0.0, 0.0, 1.0))
        joints.append({"orientation": {"x": float(q[0]), "y": float(q[1]), "z": float(q[2]), "w": float(q[3])},
                       "position": _v3(at(locals_, i, (0, 0, 0))), "scale": _v3(at(scales, i, (1, 1, 1)))})
    value = {
        "hierarchyId": guid, "jointNames": hashes, "jointParentIndices": list(parents), "unbendableJoints": [],
        "skinToJointMatrices": skin, "jointCount": n,
        "defaultPose": {"data": _blocks(pose), "alignedJointCount": aligned}, "defaultPoseJoints": joints,
    }
    return value, hashes


_SPEED = {"useInputId": False,
          "inputIdController": {"inputName": ZERO, "inputHash": {"value": 0}, "inputValueA": 0.0, "inputValueB": 1.0,
                                "speedFactorA": 1.0, "speedFactorB": 2.0},
          "rawValueController": {"speedMultiplier": 1.0, "exposeAsPin": False, "inputHash": {"value": 0}}}


def _speed(multiplier=1.0, input_guid=None, a=(0.0, 1.0), b=(1.0, 2.0)):
    if input_guid is None:
        if multiplier == 1.0:
            return _SPEED
        return dict(_SPEED, rawValueController={"speedMultiplier": float(multiplier), "exposeAsPin": False, "inputHash": {"value": 0}})
    return dict(_SPEED, useInputId=True,
                inputIdController={"inputName": input_guid, "inputHash": {"value": fnv_guid(input_guid)}, "inputValueA": float(a[0]),
                                   "inputValueB": float(b[0]), "speedFactorA": float(a[1]), "speedFactorB": float(b[1])})


class Nodes:
    """Writes runtime graph nodes, all fields as the game's converter does (checked against the game's data in
    tools/kfc/tests/test_anim.py). Ids are FNV-1a of the node's name in this graph, so a node can be named before it is
    written (a state names its parent machine, which comes after its states). Inputs must be written before their users:
    every graph of the game has that order."""

    def __init__(self, prefix="kiln"):
        self.prefix = prefix
        self.nodes = []
        self.used = {}

    def nid(self, name):
        return fnv1a32((self.prefix + "." + name).encode())

    def _add(self, kind, name, **fields):
        i = self.nid(name)
        if i in self.used:
            raise ValueError(f"node id collision: {name} and {self.used[i]}")
        self.used[i] = name
        self.nodes.append({"$type": NODE + kind + "NodeDefinition", "value": dict({"id": {"value": i}, "type": kind}, **fields)})
        return i

    def clip(self, name, clip_guid, duration, loop=True, root_motion=False, has_sequences=False, speed=None, additive=None,
             additive_frame=0, root_rotation=False, localize_root=False):
        """`additive`: the reference clip's GUID for a clip played as an additive delta (AdditiveType local space)."""
        ch = fnv_guid(clip_guid)
        ref = additive or clip_guid
        return self._add("SampleAnimationClip", name, loop=loop, animationDuration=float(duration), animation=clip_guid,
                         animationGenderAlternative=ZERO, animationHash={"value": ch}, animationGenderAlternativeHash={"value": 0},
                         rootMotionAnimation=clip_guid if root_motion else ZERO, rootMotionAnimationGenderAlternative=ZERO,
                         rootMotionAnimationHash={"value": ch if root_motion else 0}, rootMotionAnimationGenderAlternativeHash={"value": 0},
                         additiveType="Additive_LocalSpace" if additive else "None", additiveReferenceAnimation=ref,
                         additiveReferenceAnimationGenderAlternative=ZERO, additiveReferenceAnimationHash={"value": fnv_guid(ref)},
                         additiveReferenceAnimationGenderAlternativeHash={"value": 0}, additiveReferenceFrame=additive_frame,
                         loopMode="Auto", footstepSyncRole="None", footstepSyncPolicy="ExceptPeers", progressionPin={"value": 0},
                         animationContentAdditive=False, useRootMotionRotation=root_rotation, localizeRootMotionTranslation=localize_root,
                         hasSequences=has_sequences, sendEvents=True, preventRootMotion=False, debugPushDefaultPose=False,
                         preventFromRest=False, speedController=speed or _SPEED)

    def blend(self, name, input_hash, items, loop=True, circular=True, smoothing=0.2, additive=None, speed=None, fixed=False):
        """A 1D blend space. `items`: [(value, clip GUID, duration, has_sequences)], by rising value."""
        its = [{"mode": "Regular", "parameterValue": float(v), "applySpeedMultiplier": True, "animationDuration": float(d),
                "animation": g, "animationGenderAlternative": ZERO, "animationHash": {"value": fnv_guid(g)},
                "animationGenderAlternativeHash": {"value": 0}, "hasSequences": bool(h), "inputId": {"value": 0}}
               for v, g, d, h in items]
        ref = additive or ZERO
        return self._add("BlendSpace_1D", name, items=its, useParameterInputPin=False, parameterHash={"value": input_hash},
                         parameterPinInputHash={"value": 0}, loopMode="Auto", additiveType="Additive_LocalSpace" if additive else "None",
                         additiveReferenceAnimation=ref, additiveReferenceAnimationGenderAlternative=ZERO,
                         additiveReferenceAnimationHash={"value": fnv_guid(ref) if additive else 0},
                         additiveReferenceAnimationGenderAlternativeHash={"value": 0}, additiveReferenceFrame=0,
                         speedController=speed or _SPEED, eventBehaviour="SendHigherPriorityEvents",
                         interpolationSpeed=float(smoothing), keepInputValueFixed=fixed, circularInputValue=circular,
                         allowDirectBlending=False, directBlendingThreshold=90.0, directBlendingDuration=0.3, loop=loop,
                         animationContentAdditive=False, preventFromRest=False)

    def empty_additive(self, name):
        return self._add("EmptyAdditivePose", name)

    def pose(self, name, input_id):
        return self._add("PoseResult", name, input={"value": input_id})

    def state(self, name, debug_name, pose_id, machine_name, retrigger=0.2, restart=True, random_start=False):
        return self._add("StateMachineState", name, debugName=debug_name, poseResult={"value": pose_id}, idReference={"value": 0},
                         parentStateMachineId={"value": self.nid(machine_name)}, retriggerBlendDuration=float(retrigger),
                         restartOnRetrigger=restart, syncStartTime=False, startAtRandomTime=random_start, restState=False,
                         preventFromRest=False, alwaysSendEvents=False)

    def id_equals(self, name, input_hash, value):
        return self._add("IdParameterEqualsConstant", name, parameterHash={"value": input_hash}, parameterId={"value": input_hash},
                         value={"value": value}, ignoreForAnimationSequences=False)

    def id_equals_any(self, name, input_hash, values):
        return self._add("IdParameterEqualsConstantList", name, parameterHash={"value": input_hash}, parameterId={"value": input_hash},
                         values=[{"value": v} for v in values], ignoreForAnimationSequences=False)

    def overlay_active(self, name, overlay_hash):
        return self._add("IsOverlayActive", name, overlay={"value": overlay_hash})

    def transition(self, name, from_state, to_state, duration, condition=0, auto=False):
        return self._add("StateMachineTransition", name, fromState={"value": from_state}, toState={"value": to_state},
                         duration=float(duration), priority=127, conditionNode={"value": condition},
                         rootMotionMode="BlendRootMotions", eventMode="SendEventFromHighestWeightState", autoTransition=auto,
                         blendCurve="Linear", blendProfile=ZERO)

    def machine(self, name, debug_name, states, entry, parent_state=0, global_transitions=(), transitions=()):
        """`global_transitions`: [(to state id, duration, condition id)], at most MAX_GLOBAL."""
        if len(global_transitions) > MAX_GLOBAL:
            raise ValueError(f"state machine {debug_name}: {len(global_transitions)} global transitions, the game holds {MAX_GLOBAL}")
        gt = [{"toState": {"value": t}, "duration": float(d), "rootMotionMode": "BlendRootMotions",
               "eventMode": "SendEventFromHighestWeightState", "conditionNode": {"value": c}, "blendCurve": "Linear",
               "blendProfile": ZERO} for t, d, c in global_transitions]
        blank = {"toState": {"value": 0}, "duration": 0.0, "rootMotionMode": "BlendRootMotions",
                 "eventMode": "SendEventFromHighestWeightState", "conditionNode": {"value": 0}, "blendCurve": "Linear",
                 "blendProfile": ZERO}
        return self._add("StateMachine", name, states=[{"value": x} for x in states], transitions=[{"value": x} for x in transitions],
                         transitionRefInfo={"fromState": [], "toState": [], "duration": []}, entryState={"value": entry},
                         parentStateMachineStateId={"value": parent_state}, globalTransitions=gt + [blank] * (MAX_GLOBAL - len(gt)),
                         numGlobalTransitions=len(gt), globalTransitionNodeId={"value": self.nid(name + ".global")},
                         debugName=debug_name, preventFromRest=False)

    def layer(self, name, base, layer, additive=True, weight=1.0, bone_mask=ZERO, character_space=False):
        return self._add("LayerBlend", name, base={"value": base}, layer={"value": layer},
                         blendOptions={"blendMode": "Additive" if additive else "Override",
                                       "rotationMode": "CharacterSpace" if character_space else "LocalSpace",
                                       "boneMask": bone_mask, "layerWeight": float(weight)})


def graph_value(nodes, skeleton, root, slots=None, floats=(), bools=(), ids=(STATE_INPUT,), used=()):
    """The AnimationGraphResource2_0 value around written nodes. `root`: the id of a PoseResult; `slots`: {slot index: joint}."""
    table = [0] * 36
    for i, j in (slots or {}).items():
        table[i] = j
    plist = lambda xs: {"parameterIds": [{"value": x} for x in xs]}  # noqa: E731
    return {"floatParameters": plist(floats), "uintParameters": plist(()), "intParameters": plist(()), "boolParameters": plist(bools),
            "idParameters": plist(ids), "usedInputIds": plist(used), "hierarchy": skeleton, "slotBoneIndexMapping": table,
            "rootNode": {"value": root}, "postProcessRootNode": {"value": 0}, "bipedFeetIkNodeId": {"value": 0},
            "genericFeetIkNodeId": {"value": 0}, "numGenericFeetIKCount": 0, "hasGenderMapping": False,
            "nodeDefinitions": nodes.nodes, "clothColliderReference": ZERO, "ikChains": [], "syncNodes": [],
            "feetIKSettingsReference": ZERO}


class GraphBuilder:
    """One state machine of clips, each state entered when the state input holds that state's id: the shape of the game's
    creature graphs (SM_Alive), without blends and layers (tools/animcook has those). At most MAX_GLOBAL states."""

    def __init__(self, skeleton, slots=None):
        self.skeleton = skeleton
        self.slots = slots or {}
        self.n = Nodes("kiln.graph")
        self.states = []

    def state(self, name, state_guid, clip_guid, duration, loop=True, blend=0.2, root_motion=False):
        """`state_guid`: the StateId sequences and the AI name (FNV-1a of its bytes is what the graph compares)."""
        n = self.n
        c = n.clip("clip." + name, clip_guid, duration, loop=loop, root_motion=root_motion)
        p = n.pose("pose." + name, c)
        s = n.state("state." + name, name, p, "sm", retrigger=blend)
        cond = n.id_equals("cond." + name, STATE_INPUT, fnv_guid(state_guid))
        self.states.append((s, state_guid, blend, cond))

    def value(self):
        if not self.states or len(self.states) > MAX_GLOBAL:
            raise ValueError(f"a state machine has 1..{MAX_GLOBAL} states")
        n = self.n
        sm = n.machine("sm", "kiln", [x[0] for x in self.states], self.states[0][0],
                       global_transitions=[(s, b, c) for s, _, b, c in self.states])
        root = n.pose("pose.root", sm)
        return graph_value(n, self.skeleton, root, self.slots, used=[fnv_guid(g) for _, g, _, _ in self.states])


def describe_graph(v):
    """Readable summary of a decoded AnimationGraphResource2_0."""
    nodes = {n["value"]["id"]["value"]: n["value"] for n in v["nodeDefinitions"]}
    out = [f"skeleton {v['hierarchy']}, {len(nodes)} nodes, float inputs "
           f"{[hex(p['value']) for p in v['floatParameters']['parameterIds']]}, id inputs "
           f"{[hex(p['value']) for p in v['idParameters']['parameterIds']]}"]

    def clip_of(pose_id):
        p = nodes.get(pose_id)
        i = nodes.get(p["input"]["value"]) if p else None
        if not i:
            return "?"
        if i.get("type") == "SampleAnimationClip":
            return f"clip {i['animation']} {i['animationDuration']:.2f}s" + (" loop" if i["loop"] else "")
        if i.get("type") == "BlendSpace_1D":
            return f"blend space on {i['parameterHash']['value']:#x}, {len(i['items'])} clips"
        return i.get("type", "?")

    for n in v["nodeDefinitions"]:
        val = n["value"]
        if val.get("type") != "StateMachine":
            continue
        out.append(f"state machine {val.get('debugName')!r}, entry "
                   f"{nodes[val['entryState']['value']].get('debugName')}")
        by_state = {}
        for g in val["globalTransitions"][:val["numGlobalTransitions"]]:
            c = nodes.get(g["conditionNode"]["value"], {})
            if c.get("type") == "IdParameterEqualsConstant":
                by_state[g["toState"]["value"]] = hex(c["value"]["value"])
        for s in val["states"]:
            st = nodes[s["value"]]
            out.append(f"  {st.get('debugName')}: {clip_of(st['poseResult']['value'])}"
                       + (f", entered when state = {by_state[s['value']]}" if s["value"] in by_state else ""))
    return "\n".join(out)
