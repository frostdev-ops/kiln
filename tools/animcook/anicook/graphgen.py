# SPDX-License-Identifier: MIT
"""A flat description of states, blends and layers -> keen::anim_graph::runtime_graph::AnimationGraphResource2_0.

The modder lists states. This module hides what the game's format wants: more than 34 states become nested machines, the way the
game's player graph does it (an outer transition on a list of inner state ids). Rules and evidence: docs/re/npc-internals.md 2.4, 2.5."""
import uuid

from kfc.animgraph import MAX_GLOBAL, STATE_INPUT, Nodes, graph_value, fnv_guid
from kfc.util import fnv1a32, guid_bytes

BLOCK_NS = uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77")  # Kiln.Blocks' BlockIds: the same rule, so C# handles get the same ids


def derive(mod, kind, name):
    return str(uuid.uuid5(BLOCK_NS, f"{mod}:{kind}:{name}"))


# The state ids the engine itself sets (locomotion_animation's table; `Dead`, `Stun` from the game's own sequences). docs/re/npc-internals.md 2.5
ENGINE_STATES = {"Idle": 0x1490BED7, "Walk": 0x1A8DBBBB, "Run": 0x28689701, "Sprint": 0x25369C6D, "IdleCombat": 0x5D8FED2B, "Fall": 0xDD657BE9,
                 "IdleSneak": 0x30387ABB, "Sneak": 0x230EE7D7, "Dead": 0x5E840C07, "Stun": 0xE5699BCD}
# GUIDs of those the game's sequences name; a sequence can only name a state by GUID
ENGINE_GUIDS = {"Idle": "2e701760-3fb1-4e08-b14a-4e012cac15d0", "Walk": "a26eaa64-1902-496c-bc67-070c87a32303",
                "IdleCombat": "3f4d9c3e-9ca3-45aa-8f84-7249920bc26b", "Fall": "979ffb20-6612-4c72-a443-b7d99cc25688",
                "Dead": "7e2728b2-32b2-4481-a470-5d5848409427", "Stun": "1477d689-8d8c-424c-bb5e-6f4cf42b2ac8"}
# Float inputs locomotion_animation writes (hash = FNV-1a of a GUID in the exe). Unknown names are the mod's own inputs.
ENGINE_FLOATS = {"moveDirection": 0x28BB2FF6, "hitDirection": 0x232A4502, "leanYaw": 0x399D81A5, "leanPitch": 0xA6A829B0, "lookYaw": 0x6728F981,
                 "lookPitch": 0x3C857884, "targetLookYaw": 0x8A0FF150, "targetLookPitch": 0xE8AAD2A8, "headYaw": 0x198E319E, "headPitch": 0x85218118}
OVERLAYS = {"hit": 0xC6B6F9D4}  # HitReactionConfig.hitReactionOverlay of the game's creatures
SLOTS = ["Root", "Head", "Chest", "Pelvis", "FootL", "FootR", "FootRL", "FootRR", "HandR", "HandL", "KneeR", "KneeL", "ArmL", "Block", "HandPalmR",
         "HandPalmL"] + [f"Generic{i:02d}" for i in range(20)]
# AnimationGraphInputs holds 16 floats per entity. On a body with Locomotion the engine fills 12 of them by itself (lab: a standing Vukah), so a graph's own inputs get what is left.
ENGINE_FLOATS_IN_USE = {0x28BB2FF6, 0xE9046158, 0x31E304E7, 0x3C857884, 0xE8AAD2A8, 0x8A0FF150, 0x198E319E, 0x85218118, 0x251EA4F4, 0x399D81A5, 0xA6A829B0, 0xAC322F74}
LIMITS = {"floats": 16}
DEFAULT_BLEND = 0.2


class GraphError(Exception):
    pass


def _hash_of(v):
    return int(v, 16) if isinstance(v, str) and v.startswith("0x") else fnv1a32(guid_bytes(v))


class Compiler:
    def __init__(self, mod, name, spec, skeleton, clips):
        """`clips`: {name: {guid, duration, loop, rootMotion, rootRotation, events}}. `skeleton`: a Skeleton with .guid."""
        self.mod, self.name, self.spec, self.skeleton, self.clips = mod, name, spec, skeleton, clips
        self.n = Nodes(f"{mod}.{name}")
        self.floats = []
        self.state_ids = {}
        self.masks = []

    def err(self, msg):
        return GraphError(f"graph {self.name}: {msg}")

    def clip(self, ref, where):
        c = self.clips.get(ref)
        if c is None:
            raise self.err(f"{where}: no clip {ref!r} (this recipe has {', '.join(sorted(self.clips)) or 'none'})")
        return c

    def float_input(self, name, where):
        if isinstance(name, str) and name.startswith("0x"):
            h = int(name, 16)
        elif name in ENGINE_FLOATS:
            h = ENGINE_FLOATS[name]
        else:
            h = fnv_guid(derive(self.mod, "input", name))
        if h not in self.floats:
            self.floats.append(h)
        extra = len(set(self.floats) - ENGINE_FLOATS_IN_USE)
        free = LIMITS["floats"] - len(ENGINE_FLOATS_IN_USE)
        if extra > free:
            raise self.err(f"{where}: {extra} float inputs of its own. An entity holds {LIMITS['floats']} and the engine fills {len(ENGINE_FLOATS_IN_USE)} on a creature that moves: {free} are left")
        return h

    def state_identity(self, name, st):
        """-> (state input value, GUID or None): the engine's member, a given id, or this mod's own."""
        if "engine" in st:
            e = st["engine"]
            if e not in ENGINE_STATES:
                raise self.err(f"state {name}: engine state {e!r} (known: {', '.join(ENGINE_STATES)})")
            return ENGINE_STATES[e], ENGINE_GUIDS.get(e)
        if "id" in st:
            v = st["id"]
            return _hash_of(v), None if v.startswith("0x") else v
        g = derive(self.mod, "state", name)
        return fnv_guid(g), g

    def pose_source(self, key, st, where, additive=None):
        """A clip node or a blend space for a state or layer. Returns (node id, duration)."""
        n = self.n
        speed = None
        if "speed" in st:
            from kfc.animgraph import _speed
            speed = _speed(float(st["speed"]))
        if "clip" in st and "blend" in st:
            raise self.err(f"{where}: clip or blend, not both")
        if "clip" in st:
            c = self.clip(st["clip"], where)
            loop = st.get("loop", c["loop"])
            return n.clip(f"clip.{key}", c["guid"], c["duration"], loop=loop, root_motion=bool(c.get("rootMotion")),
                          has_sequences=bool(c.get("events")), speed=speed, additive=additive,
                          root_rotation=bool(c.get("rootRotation"))), c["duration"]
        if "blend" in st:
            b = st["blend"]
            if "input" not in b or not b.get("clips"):
                raise self.err(f"{where}: a blend needs input and clips")
            items = []
            for v, ref in sorted(((float(k), r) for k, r in b["clips"].items()), key=lambda x: x[0]):
                c = self.clip(ref, where)
                items.append((v, c["guid"], c["duration"], bool(c.get("events"))))
            durs = {round(i[2], 4) for i in items}
            if len(durs) > 1:
                self.warnings.append(f"{where}: blended clips differ in length ({', '.join(str(d) for d in sorted(durs))} s): feet may slide")
            h = self.float_input(b["input"], where)
            first = self.clip(next(iter(b["clips"].values())), where)
            return n.blend(f"blend.{key}", h, items, loop=st.get("loop", first["loop"]), circular=bool(b.get("circular", True)),
                           smoothing=float(b.get("smoothing", 0.2)), additive=additive, speed=speed), items[0][2]
        raise self.err(f"{where}: needs clip or blend")

    def leaf(self, name, st):
        """Writes one state's source, pose and condition. The state node itself follows when its machine is known."""
        value, guid = self.state_identity(name, st)
        if value in {x[0] for x in self.state_ids.values()}:
            other = next(k for k, x in self.state_ids.items() if x[0] == value)
            raise self.err(f"states {other} and {name} have the same state id {value:#x}")
        self.state_ids[name] = (value, guid, st.get("engine"))
        src, dur = self.pose_source(f"state.{name}", st, f"state {name}")
        pose = self.n.pose(f"pose.{name}", src)
        cond = self.n.id_equals(f"cond.{name}", STATE_INPUT, value)
        return {"name": name, "pose": pose, "cond": cond, "value": value, "blend": float(st.get("blendIn", DEFAULT_BLEND)),
                "restart": bool(st.get("restart", True)), "then": st.get("then"), "thenBlend": float(st.get("thenBlend", st.get("blendIn", DEFAULT_BLEND))),
                "duration": dur}

    def chains(self, leaves):
        """Items of a machine, in the order given: a plain leaf, or a chain (a `then` sequence) as a list of leaves. A chain becomes its own
        machine: the state input stays on the first state's id while the chain moves on, and a global transition on that id would pull it
        straight back, so inside the chain the first state has none. The game's hit flinch and nested machines work the same way."""
        by_name = {lf["name"]: lf for lf in leaves}
        targets = {}
        for lf in leaves:
            t = lf["then"]
            if not t:
                continue
            if t not in by_name:
                raise self.err(f"state {lf['name']}: then {t!r} is not a state")
            if t in targets:
                raise self.err(f"states {targets[t]} and {lf['name']} both go on to {t}: a state has one way in")
            targets[t] = lf["name"]
        items, done = [], set()
        for lf in leaves:
            if lf["name"] in done:
                continue
            if lf["then"] is None and lf["name"] not in targets:
                items.append(lf)
                done.add(lf["name"])
                continue
            head = lf
            seen = {head["name"]}
            while head["name"] in targets and targets[head["name"]] not in seen:
                head = by_name[targets[head["name"]]]
                seen.add(head["name"])
            chain, cur = [], head
            while cur is not None and cur["name"] not in {x["name"] for x in chain}:
                chain.append(cur)
                cur = by_name.get(cur["then"]) if cur["then"] else None
            items.append(chain)
            done.update(x["name"] for x in chain)
        return items

    def chain_machine(self, chain, parent_state):
        n = self.n
        mname = f"sm.chain.{chain[0]['name']}"
        ids = {lf["name"]: n.state(f"state.{lf['name']}", lf["name"], lf["pose"], mname, retrigger=lf["blend"], restart=lf["restart"]) for lf in chain}
        trans = [n.transition(f"then.{lf['name']}", ids[lf["name"]], ids[lf["then"]], lf["thenBlend"], auto=True) for lf in chain if lf["then"]]
        gt = [(ids[lf["name"]], lf["blend"], lf["cond"]) for lf in chain[1:]]
        return n.machine(mname, chain[0]["name"], [ids[lf["name"]] for lf in chain], ids[chain[0]["name"]], parent_state=parent_state,
                         global_transitions=gt, transitions=trans)

    def machine(self, mname, debug, items, entry_name, parent_state=0):
        """One machine over at most MAX_GLOBAL items. Returns its id."""
        n = self.n
        states, gt, entry = [], [], None
        for it in items:
            if isinstance(it, list):
                outer = f"chain.{it[0]['name']}"
                inner = self.chain_machine(it, n.nid(outer))
                pose = n.pose(f"pose.{outer}", inner)
                s = n.state(outer, it[0]["name"], pose, mname, retrigger=it[0]["blend"], restart=True)
                cond = n.id_equals_any(f"cond.{outer}", STATE_INPUT, [lf["value"] for lf in it])
                gt.append((s, it[0]["blend"], cond))
                names = [lf["name"] for lf in it]
            else:
                s = n.state(f"state.{it['name']}", it["name"], it["pose"], mname, retrigger=it["blend"], restart=it["restart"])
                gt.append((s, it["blend"], it["cond"]))
                names = [it["name"]]
            states.append(s)
            if entry_name in names:
                entry = s
        return n.machine(mname, debug, states, entry if entry is not None else states[0], parent_state=parent_state, global_transitions=gt)

    def base_machine(self, leaves, entry):
        n = self.n
        items = self.chains(leaves)
        if len(items) <= MAX_GLOBAL:
            return self.machine("sm", self.name, items, entry), False
        groups = [items[i:i + MAX_GLOBAL] for i in range(0, len(items), MAX_GLOBAL)]
        if len(groups) > MAX_GLOBAL:
            raise self.err(f"{len(leaves)} states: at most {MAX_GLOBAL * MAX_GLOBAL}")
        outer_states, gt, outer_entry = [], [], None
        for gi, group in enumerate(groups):
            flat = [x for it in group for x in (it if isinstance(it, list) else [it])]
            outer = f"group.{gi}"
            inner = self.machine(f"sm.{gi}", f"group{gi}", group, entry, parent_state=n.nid(outer))
            pose = n.pose(f"pose.{outer}", inner)
            s = n.state(outer, f"group{gi}", pose, "sm", retrigger=DEFAULT_BLEND, restart=False)
            cond = n.id_equals_any(f"cond.{outer}", STATE_INPUT, [lf["value"] for lf in flat])
            outer_states.append(s)
            gt.append((s, max(lf["blend"] for lf in flat), cond))
            if entry in [lf["name"] for lf in flat]:
                outer_entry = s
        return n.machine("sm", self.name, outer_states, outer_entry, global_transitions=gt), True

    def bone_mask(self, layer_name, bones):
        """bones: {joint name: weight} or [joint names]. -> the mask's GUID; the resource value goes to self.masks."""
        items = bones.items() if isinstance(bones, dict) else [(b, 1.0) for b in bones]
        entries = []
        for b, w in items:
            if b not in self.skeleton.index:
                raise self.err(f"layer {layer_name}: joint {b!r} is not in the skeleton")
            entries.append({"boneIndex": self.skeleton.index[b], "weight": float(w)})
        g = derive(self.mod, "mask", f"{self.name}.{layer_name}")
        self.masks.append((f"{self.name}.{layer_name}", g, {"entries": entries}))
        return g

    def layer(self, base, spec):
        """Over `base`, one of: {clip|blend} playing always, or {overlay, clip|blend} played when an overlay fires, as the game's
        hit flinch (rest, play, replay)."""
        n = self.n
        lname = spec.get("name")
        if not lname:
            raise self.err("a layer needs a name")
        where = f"layer {lname}"
        mode = spec.get("mode", "additive")
        if mode not in ("additive", "override"):
            raise self.err(f"{where}: mode {mode!r}")
        additive = mode == "additive"
        ref = None
        if additive:
            r = spec.get("reference")
            if r is None:
                raise self.err(f"{where}: an additive layer needs reference, the clip whose pose it is relative to")
            ref = self.clip(r, where)["guid"]
        src, _ = self.pose_source(f"layer.{lname}", spec, where, additive=ref)
        top = src
        if "overlay" in spec:
            ov = spec["overlay"]
            oh = OVERLAYS.get(ov) if ov in OVERLAYS else (int(ov, 16) if ov.startswith("0x") else fnv_guid(derive(self.mod, "overlay", ov)))
            sm = f"layer.{lname}.sm"
            rest = n.empty_additive(f"layer.{lname}.empty") if additive else n._add("EmptyOverridePose", f"layer.{lname}.empty")
            rest_pose = n.pose(f"layer.{lname}.rest.pose", rest)
            rest_state = n.state(f"layer.{lname}.rest", "rest", rest_pose, sm, retrigger=0.25)
            play_pose = n.pose(f"layer.{lname}.play.pose", src)
            play = n.state(f"layer.{lname}.play", "play", play_pose, sm, retrigger=0.3, restart=False)
            again_src, _ = self.pose_source(f"layer.{lname}.again", spec, where, additive=ref)
            again_pose = n.pose(f"layer.{lname}.again.pose", again_src)
            again = n.state(f"layer.{lname}.again", "again", again_pose, sm, retrigger=0.3, restart=False)
            fade = float(spec.get("fadeOut", 0.25))
            fade_in = float(spec.get("fadeIn", 0.05))
            tr = [n.transition(f"layer.{lname}.again.out", again, rest_state, fade, auto=True),
                  n.transition(f"layer.{lname}.again.play", again, play, fade_in, n.overlay_active(f"layer.{lname}.c1", oh)),
                  n.transition(f"layer.{lname}.play.out", play, rest_state, fade, auto=True),
                  n.transition(f"layer.{lname}.play.again", play, again, fade_in, n.overlay_active(f"layer.{lname}.c2", oh)),
                  n.transition(f"layer.{lname}.rest.play", rest_state, play, fade_in, n.overlay_active(f"layer.{lname}.c3", oh))]
            top = n.machine(sm, lname, [rest_state, play, again], rest_state, transitions=tr)
            self.overlays[lname] = oh
        mask = "00000000-0000-0000-0000-000000000000"
        if spec.get("bones"):
            mask = self.bone_mask(lname, spec["bones"])
        return n.layer(f"layer.{lname}", base, top, additive=additive, weight=float(spec.get("weight", 1.0)), bone_mask=mask)

    def compile(self):
        spec = self.spec
        self.warnings = []
        self.overlays = {}
        states = spec.get("states") or {}
        if not states:
            raise self.err("no states")
        names = list(states)
        entry = spec.get("entry") or next((k for k, v in states.items() if v.get("engine") == "Idle"), names[0])
        if entry not in states:
            raise self.err(f"entry {entry!r} is not a state")
        leaves = [self.leaf(k, v) for k, v in states.items()]
        top, nested = self.base_machine(leaves, entry)
        for ls in spec.get("layers") or []:
            top = self.layer(top, ls)
        root = self.n.pose("pose.root", top)
        slots = {}
        for slot, joint in (spec.get("slots") or {}).items():
            if slot not in SLOTS:
                raise self.err(f"slot {slot!r} (known: {', '.join(SLOTS[:16])}, Generic00..19)")
            if joint not in self.skeleton.index:
                raise self.err(f"slot {slot}: joint {joint!r} is not in the skeleton")
            slots[SLOTS.index(slot)] = self.skeleton.index[joint]
        used = [x[0] for x in self.state_ids.values()]
        value = graph_value(self.n, self.skeleton.guid, root, slots, floats=self.floats, used=used)
        info = {"states": {k: {"id": f"0x{v[0]:08x}", "guid": v[1], "engine": v[2]} for k, v in self.state_ids.items()},
                "entry": entry, "nested": nested, "nodes": len(self.n.nodes),
                "floatInputs": [f"0x{h:08x}" for h in self.floats], "overlays": {k: f"0x{v:08x}" for k, v in self.overlays.items()},
                "slots": {SLOTS[i]: j for i, j in slots.items()}, "warnings": self.warnings}
        return value, info


def check_fields(types, value):
    """Every node has exactly the reflection's fields, so a missing one cannot silently be zero."""
    for n in value["nodeDefinitions"]:
        t = types.get(n["$type"])
        if t is None:
            raise GraphError(f"unknown node type {n['$type']}")
        want = {f["name"] for f in types.all_fields(t)}
        have = set(n["value"])
        if want != have:
            raise GraphError(f"{n['$type']}: fields differ from the game's: {sorted(want ^ have)}")
