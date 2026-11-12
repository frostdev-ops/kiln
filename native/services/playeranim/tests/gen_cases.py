#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The Python side of kiln.playeranim's host test (run.sh): runs the reference (tools/animcook/anicook/playerstates.py and
playergraph.py, tools/kfc's codec) on every input the Python tests use, and writes the inputs and the reference's outputs for
test_playeranim.cpp to compare with the C++ port, byte for byte and report for report.

    python3 -I gen_cases.py --out DIR [--graph FILE.bin] [--game DIR]

DIR gets cases.json and the graphs and type dumps it names. --graph: the game's player graph (game data: read by path, the
merged copies stay in DIR). --game: the installed game, for the real clips' facts and the other-skeleton clip; without it the
facts come from the graph's own clip nodes, as game_playergraph_check.py does.
"""
import argparse
import json
import random
import struct
import sys
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
sys.path[:0] = [str(REPO / "tools" / "animcook"), str(REPO / "tools" / "animcook" / "tests"), str(REPO / "tools" / "kfc"), str(REPO / "tools" / "modelcook")]

import test_playergraph as TP  # noqa: E402  (the synthetic player-like graph and the facts of the Python tests)
from anicook import graphgen as G, playergraph as PG, playerstates as PS  # noqa: E402
from kfc import animgraph as A  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

TYPES = TP.TYPES
BUNDLED = REPO / "tools" / "animcook" / "types" / "anim-types.json"
GRAPH = PG.GRAPH_TYPE


def wtf(s):
    """A Python str as hex of its UTF-8 bytes, lone surrogates as three bytes (they cannot travel in strict JSON)."""
    return None if s is None else s.encode("utf-8", "surrogatepass").hex()


def decode(raw, types=TYPES):
    return Decoder(types, max_array=PG.MAX_ARRAY).decode(types.get(GRAPH), raw)


def encode(value, types=TYPES):
    return Encoder(types).encode(types.get(GRAPH), value)


class Out:
    def __init__(self, d):
        self.dir = d
        d.mkdir(parents=True, exist_ok=True)
        self.cases = {"types": str(BUNDLED), "codec": [], "text": [], "repr": [], "floatrepr": [], "parse": [], "manifest": [], "merge": [],
                      "validate": [], "jointanim": [], "fuzz": [], "game": None}
        self.files = {}

    def file(self, name, data):
        (self.dir / name).write_bytes(data)
        return name

    def graph(self, name, raw):
        if name not in self.files:
            self.files[name] = self.file(f"{name}.bin", raw)
        return self.files[name]

    def types(self, name, types):
        p = f"types-{name}.json"
        (self.dir / p).write_text(json.dumps(types.dump))
        return p


# ---- inputs as the C++ side reads them ----

def mod_input(states, name="m", side="both", clients="mod", source=PS.FILE_NAME, text=None):
    return {"text": text if text is not None else json.dumps({"format": 1, "states": states}), "mod": name, "side": side, "clients": clients,
            "source": source}


def parse(mi):
    return PS.parse(mi["text"], mi["mod"], mi["side"], mi["clients"], source=mi["source"])


def facts_json(facts):
    return {g: (None if f is None else {k: (bool(v) if isinstance(v, bool) else v) for k, v in f.items()}) for g, f in facts.items()}


def fake_node_id(*p):
    return 77 if p[1:] in (("p", "clip"), ("q", "cond")) else REAL_NODE_ID(*p)


REAL_NODE_ID = PG.node_id


class Lossy(Encoder):
    calls = 0

    def encode(self, t, v):
        Lossy.calls += 1
        out = super().encode(t, v)
        return out + b"\0" if Lossy.calls == 2 else out


def merge_case(out, name, graph_name, raw, mods, facts, types=TYPES, types_name=None, hook=None, lazy=False):
    """Runs the Python merge; records inputs and outputs."""
    called = []

    def reader():
        called.append(1)
        return raw
    ms = [parse(m) for m in mods]
    src = reader if lazy else raw
    if hook == "node_id_pq77":
        with mock.patch.object(PG, "node_id", fake_node_id):
            res, rep = PG.merge(src, types, ms, facts.get)
    elif hook == "lossy2":
        Lossy.calls = 0
        with mock.patch.object(PG, "Encoder", Lossy):
            res, rep = PG.merge(src, types, ms, facts.get)
    else:
        res, rep = PG.merge(src, types, ms, facts.get)
    case = {"name": name, "graph": out.graph(graph_name, raw), "types": types_name, "mods": mods, "facts": facts_json(facts), "hook": hook, "lazy": lazy,
            "called": bool(called), "report": rep, "bin": out.file(f"want-{len(out.cases['merge'])}.bin", res) if res is not None else None}
    out.cases["merge"].append(case)
    return res, rep


# ---- the Python tests' scenarios ----

def base_facts(synth):
    f = dict(synth.facts)
    for m in TP.Base.MODS:
        for nm in ("x", "y", "z", "slash", "guard"):
            f[TP.mclip(nm, m)] = TP.facts(fps=30, frames=47, loop=True, rm=False, seq=False)
    return f


def rules(out):
    s = TP.Synth()
    raw = s.raw()
    out.cases["codec"].append({"name": "synthetic", "graph": out.graph("synth4", raw)})
    F = base_facts(s)
    h0 = s.hammer[0]

    def m(name, states, facts=None, **kw):
        return merge_case(out, name, "synth4", raw, [mod_input(states, **kw)], facts or F)
    m("rule1 clip node", {"x": {"clip": "x", "like": h0}, "y": {"clip": "y", "like": h0, "loop": False, "speed": 1.25}})
    f2 = dict(F)
    f2[TP.mclip("z")] = TP.facts(rm=True, seq=True, fps=60, frames=61)
    m("rule1 root motion and sequences", {"z": {"clip": "z", "like": h0}}, f2)
    m("rule1 depth first, additive", {"x": {"clip": "x", "like": "Attack_Unarmed_01"}, "y": {"clip": "y", "like": "Attack_Hammer_Additive"}})
    m("rule1 no clip node", {"b": {"clip": "x", "like": "Attack_Hammer_Blend"}, "ok": {"clip": "x", "like": "Parry_FullBody"}})
    m("rule2 layer blend", {"guard": {"clip": "guard", "like": "Block_FullBody"}})
    m("rule2 plain pose", {"p": {"clip": "x", "like": "Parry_FullBody"}})
    m("rule2 layer over a clip", {"k": {"clip": "x", "like": "Attack_Skill_Kick_FullBody"}, "ok": {"clip": "x", "like": "Parry_FullBody"}})
    m("rule3 state node", {"a": {"clip": "x", "like": "Parry_FullBody", "blendIn": 0.07}, "b": {"clip": "x", "like": "Parry_FullBody"}})
    m("rule4 entry", {"a": {"clip": "x", "like": h0, "blendIn": 0.05}, "b": {"clip": "x", "like": h0}, "j": {"clip": "x", "like": "Jump_Attack_End_FullBody"}})
    m("rule4 from a local transition", {"u": {"clip": "x", "like": "Attack_Unarmed_01"}})
    m("rule4 no way in", {"l": {"clip": "x", "like": "Attack_Unarmed_Lonely"}, "ok": {"clip": "x", "like": "Attack_Unarmed_02"}})
    m("rule5 local transitions", {"d": {"clip": "x", "like": "DodgeRoll_FullBody"}, "a": {"clip": "x", "like": "Dodge_Attack_FullBody"},
                                  "s1": {"clip": "x", "like": "Attack_Sword_Shield_01"}, "s2": {"clip": "x", "like": "Attack_Sword_Shield_02"},
                                  "u": {"clip": "x", "like": "Attack_Unarmed_02"}})
    m("rule6 lists", {"h": {"clip": "x", "like": h0}, "j": {"clip": "x", "like": "Jump_Attack_Start_FullBody"}, "a": {"clip": "x", "like": "Dodge_Attack_FullBody"}})
    m("rule7 selects", {"h": {"clip": "x", "like": h0}, "s": {"clip": "x", "like": "Sneak_Attack_FullBody"}})
    m("rule9 placement", {"a": {"clip": "x", "like": "Dodge_Attack_FullBody"}, "d": {"clip": "x", "like": "DodgeRoll_FullBody"},
                          "g": {"clip": "guard", "like": "Block_FullBody"}, "h": {"clip": "x", "like": h0}})

    def fill(n, hammer=4, prefix="s"):
        return {f"{prefix}{i:02d}": {"clip": "x", "like": f"Attack_Hammer_{i % hammer:02d}"} for i in range(n)}
    num = 6
    m("rule8 fill to 34", fill(34 - num))
    m("rule8 nest", dict(fill(34 - num + 1), first={"clip": "x", "like": "Attack_Hammer_00", "blendIn": 0.05}))
    m("rule8 nested states have no local transitions", {f"u{i:02d}": {"clip": "x", "like": "Attack_Unarmed_0" + "12"[i % 2]} for i in range(35)})
    m("rule9 nested placement", fill(30))
    s31 = TP.Synth(31)
    raw31 = s31.raw()
    out.cases["codec"].append({"name": "synthetic, 31 hammer states", "graph": out.graph("synth31", raw31)})
    F31 = dict(F)
    F31.update(s31.facts)
    merge_case(out, "rule8 full", "synth31", raw31, [mod_input(fill(35, hammer=31))], F31)
    # rule 10
    val = decode(raw)
    clash = dict(val["nodeDefinitions"][0], value=dict(val["nodeDefinitions"][0]["value"], id={"value": PG.node_id("m", "x", "pose")}))
    val["nodeDefinitions"].insert(0, clash)
    raw_clash = encode(val)
    merge_case(out, "rule10 a node id the graph has", "synth4-clash", raw_clash,
               [mod_input({"x": {"clip": "x", "like": "Parry_FullBody"}, "y": {"clip": "x", "like": "Parry_FullBody"}})], F)
    val = decode(raw)
    val["usedInputIds"]["parameterIds"].append({"value": A.fnv_guid(G.derive("m", "state", "taken"))})
    merge_case(out, "rule10 a state id the graph compares", "synth4-used", encode(val),
               [mod_input({"taken": {"clip": "x", "like": "Parry_FullBody"}, "y": {"clip": "x", "like": "Parry_FullBody"}})], F)
    merge_case(out, "rule10 two of ours alike", "synth4", raw,
               [mod_input({"p": {"clip": "x", "like": "Parry_FullBody"}, "q": {"clip": "x", "like": "Parry_FullBody"}}),
                mod_input({"r": {"clip": "x", "like": "Parry_FullBody"}}, name="other")], F, hook="node_id_pq77")
    # rule 11: the order of mods and of states changes no byte
    a = {f"a{i}": {"clip": "x", "like": lk} for i, lk in enumerate(["Parry_FullBody", "Block_FullBody", "DodgeRoll_FullBody", s.hammer[1],
                                                                    "Jump_Attack_Loop_FullBody", "Attack_Unarmed_02", "Dodge_Attack_FullBody"])}
    b = {f"b{i}": {"clip": "y", "like": lk} for i, lk in enumerate(["Parry_FullBody", s.hammer[0], "Attack_Sword_Shield_02"])}
    merge_case(out, "rule11 order 0", "synth4", raw, [mod_input(a, "alpha"), mod_input(b, "beta")], F)
    rng = random.Random(7)
    for i in range(4):
        ka, kb = list(a), list(b)
        rng.shuffle(ka)
        rng.shuffle(kb)
        mods = [mod_input({k: a[k] for k in ka}, "alpha"), mod_input({k: b[k] for k in kb}, "beta")]
        rng.shuffle(mods)
        merge_case(out, f"rule11 order {i + 1}", "synth4", raw, mods, F)
    # rule 12
    for i, mods in enumerate(([], [mod_input({})], [mod_input({"a": {"clip": "x", "like": "Parry_FullBody"}}, side="server")])):
        merge_case(out, f"rule12 nothing to do {i}", "synth4", raw, mods, F, lazy=True)
    m("rule12 nothing accepted", {"a": {"clip": "x", "like": "Nope"}})
    merge_case(out, "rule12 a state left: the graph is read", "synth4", raw, [mod_input({"a": {"clip": "x", "like": "Parry_FullBody"}})], F, lazy=True)
    # refusals
    m("refusals like", {"unknown": {"clip": "x", "like": "No_Such_State"}, "ambiguous": {"clip": "x", "like": "Attack_Heavy"},
                        "qualified": {"clip": "x", "like": "SM_Attack_Dual_Daggers_FullBody/Attack_Heavy"}, "idle-block": {"clip": "x", "like": "Idle_Block"},
                        "move-block": {"clip": "x", "like": "SM_Block_Locomotion/Move_Block"}, "grounded": {"clip": "x", "like": "GroundedLocomotion_FullBody"},
                        "upper": {"clip": "x", "like": "DoNothing"}, "wrong-machine": {"clip": "x", "like": "SM_Main/Attack_Heavy"},
                        "ok": {"clip": "x", "like": "Parry_FullBody"}, "quote's": {"clip": "x", "like": "It's \"odd\" \\ \u00e9\u4e2d\U0001f600\u00a0\u200b\x7f"}})
    fc = dict(F)
    fc[TP.mclip("other")] = TP.facts(hier=0xDEAD, joints=60)
    fc[TP.mclip("nofps")] = TP.facts(fps=0)
    m("refusals clips", {"unserved": {"clip": "nope", "like": "Parry_FullBody"}, "skeleton": {"clip": "other", "like": "Parry_FullBody"},
                         "fps": {"clip": "nofps", "like": "Parry_FullBody"}, "ok": {"clip": "x", "like": "Parry_FullBody"},
                         "guid-nl": {"clip": "6DD6A543-1111-2222-3333-444455556666\n", "like": "Parry_FullBody"}}, fc)
    fp = dict(F)
    del fp[TP.cg("parry")]
    m("refusals the neighbour's clip", {"p": {"clip": "x", "like": "Parry_FullBody"}}, fp)
    merge_case(out, "refusals a mod twice", "synth4", raw, [mod_input({"a": {"clip": "x", "like": "Parry_FullBody"}}),
                                                            mod_input({"b": {"clip": "x", "like": "Parry_FullBody"}}, source="other/player-states.json"),
                                                            mod_input({"c": {"clip": "x", "like": "Parry_FullBody"}}, name="c-mod")], F)
    merge_case(out, "refusals a server mod", "synth4", raw, [mod_input({"s": {"clip": "x", "like": "Parry_FullBody"}}, name="srv", side="server"),
                                                             mod_input({"c": {"clip": "x", "like": "Parry_FullBody"}}, name="c-mod")], F)
    # off
    st = {"a": {"clip": "x", "like": "Parry_FullBody"}, "b": {"clip": "x", "like": "Parry_FullBody", "speed": 0}}
    for tname, field in ((PG.NODE + "StateMachineStateNodeDefinition", "retriggerBlendDuration"),
                         ("keen::anim_graph::AnimationSpeedControllerRawValue", "speedMultiplier"), (PG.GT_TYPE, "blendCurve")):
        dump = json.loads(json.dumps(TYPES.dump))
        t = next(x for x in dump["types"] if x["name"] == tname)
        for f in t["fields"]:
            if f["name"] == field:
                f["name"] = field + "Renamed"
        tdb = TypeDb(dump)
        merge_case(out, f"off renamed {field}", "synth4", raw, [mod_input(st)], F, types=tdb, types_name=out.types(f"renamed-{field}", tdb))
    val = decode(raw)
    next(n["value"] for n in val["nodeDefinitions"] if n["value"]["type"] == "PoseResult")["input"]["value"] = 0x0BADF00D
    merge_case(out, "off a dangling reference", "synth4-dangling", encode(val), [mod_input(st)], F)
    val = decode(raw)
    nodes = val["nodeDefinitions"]
    i = next(i for i, n in enumerate(nodes) if n["value"]["type"] == "PoseResult")
    nodes.insert(len(nodes) - 1, nodes.pop(i - 1))
    merge_case(out, "off a forward reference", "synth4-forward", encode(val), [mod_input(st)], F)
    merge_case(out, "off bytes that do not encode back", "synth4-plus4", raw + b"\0\0\0\0", [mod_input(st)], F)
    merge_case(out, "off a truncated graph", "synth4-100", raw[:100], [mod_input(st)], F)
    merge_case(out, "off an empty graph", "empty", b"", [mod_input(st)], F)
    val = decode(raw)
    val["nodeDefinitions"][1]["value"]["id"] = dict(val["nodeDefinitions"][0]["value"]["id"])
    merge_case(out, "off ids twice in the game", "synth4-twice", encode(val), [mod_input(st)], F)
    merge_case(out, "off the merged graph must decode to itself", "synth4", raw, [mod_input(st)], F, hook="lossy2")
    # a field the game adds is carried
    dump = json.loads(json.dumps(TYPES.dump))
    kinds = TP.NewFields.KINDS
    for t in dump["types"]:
        if t["name"] in {PG.full_type(k) for k in kinds}:
            off = (t["size"] + 3) // 4 * 4
            t["fields"].append({"name": "kilnExtra", "type": "keen::uint32", "offset": off})
            t["size"] = (off + 4 + t["align"] - 1) // t["align"] * t["align"]
    tx = TypeDb(dump)
    sx = TP.Synth(4)
    for n in sx.value["nodeDefinitions"]:
        if n["value"]["type"] in kinds:
            n["value"]["kilnExtra"] = 7
    raw_x = Encoder(tx).encode(tx.get(GRAPH), sx.value)
    states = {f"h{i:02d}": {"clip": "x", "like": "Attack_Hammer_00"} for i in range(30)}
    states.update(a={"clip": "x", "like": "Dodge_Attack_FullBody"}, b={"clip": "guard", "like": "Block_FullBody"}, d={"clip": "x", "like": "DodgeRoll_FullBody"})
    fx = dict(F)
    fx.update(sx.facts)
    tname = out.types("extra-field", tx)
    out.cases["codec"].append({"name": "synthetic with a field the game adds", "graph": out.graph("synth4-extra", raw_x), "types": tname})
    merge_case(out, "new fields are carried", "synth4-extra", raw_x, [mod_input(states)], fx, types=tx, types_name=tname)
    # validate on a changed merged graph (the Python test's cases): the reason of each
    res, _ = PG.merge(raw, TYPES, [parse(mod_input({"a": {"clip": "x", "like": "Parry_FullBody"}, "h": {"clip": "x", "like": h0}}))], F.get)
    merged, pristine = decode(res), decode(raw)
    new = sorted({n["value"]["id"]["value"] for n in merged["nodeDefinitions"]} - {n["value"]["id"]["value"] for n in pristine["nodeDefinitions"]})
    machines = sorted({s.n.nid("sm.main"), s.n.nid("sm.ha")})
    lists = sorted({s.n.nid(x) for x in ("l.w.ha", "l.main.melee", "l.reset")})
    selects = [s.n.nid("sel.ov")]

    def machine(v, debug):
        return next(n["value"] for n in v["nodeDefinitions"] if n["value"]["type"] == "StateMachine" and n["value"]["debugName"] == debug)

    def by_id(v):
        return {n["value"]["id"]["value"]: n["value"] for n in v["nodeDefinitions"]}

    def overfill(v):
        mm = machine(v, "SM_Main")
        gts = mm["globalTransitions"]
        gts[mm["numGlobalTransitions"]:] = [dict(gts[0]) for _ in range(A.MAX_GLOBAL - mm["numGlobalTransitions"])]
        mm["numGlobalTransitions"] = A.MAX_GLOBAL + 1
    changes = {
        "none": lambda v: None,
        "a game node": lambda v: v["nodeDefinitions"][0]["value"].update(loop=not v["nodeDefinitions"][0]["value"]["loop"]),
        "a machine's own transitions": lambda v: machine(v, "SM_Main")["globalTransitions"][0].update(duration=9.0),
        "a list lost an id": lambda v: by_id(v)[s.reset]["values"].pop(0),
        "the header": lambda v: v.update(hasGenderMapping=not v["hasGenderMapping"]),
        "usedInputIds": lambda v: v["usedInputIds"]["parameterIds"].pop(0),
        "too many": overfill,
        "count": lambda v: machine(v, "SM_Main").update(numGlobalTransitions=machine(v, "SM_Main")["numGlobalTransitions"] - 1),
        "shape": lambda v: by_id(v)[PG.node_id("m", "a", "clip")]["speedController"]["rawValueController"].update(extra=1),
        "a missing field": lambda v: by_id(v)[PG.node_id("m", "a", "state")].pop("restState"),
        "a game node gone": lambda v: v["nodeDefinitions"].pop(0),
    }
    results = {}
    for what, change in changes.items():
        bad = decode(res)
        change(bad)
        try:
            PG.validate(TYPES, pristine, bad, set(machines), set(lists), set(selects), set(new))
            results[what] = None
        except PG.MergeOff as e:
            results[what] = str(e)
    out.cases["validate"].append({"graph": out.graph("synth4", raw), "merged": out.file("validate-merged.bin", res), "machines": machines, "lists": lists,
                                  "selects": selects, "new": new, "reset": s.reset, "clip": PG.node_id("m", "a", "clip"), "state": PG.node_id("m", "a", "state"),
                                  "results": results})
    return s, raw, F


def definitions(out):
    note = TP.Definitions.NOTE

    def p(name, text=None, mod="m", side="both", clients="mod", source=PS.FILE_NAME, raw=None, hook=None):
        case = {"name": name, "mod": mod, "side": side, "clients": clients, "source": source, "hook": hook}
        if raw is not None:
            case["bytes"] = raw.hex()
        else:
            case["text"] = text
        try:
            if hook == "state_id_1234":
                with mock.patch.object(PS, "fnv_guid", return_value=0x1234):
                    ms = PS.parse(text, mod, side, clients, source=source)
            elif raw is not None:
                ms = load_bytes(raw, mod, side, clients, source)
            else:
                ms = PS.parse(text, mod, side, clients, source=source)
            case["states"] = [{"name": wtf(s.name), "mod": wtf(s.mod), "guid": s.guid, "id": s.id, "clip_ref": wtf(s.clip_ref), "clip": wtf(s.clip),
                               "like": wtf(s.like), "blend_in": s.blend_in, "loop": s.loop, "speed": s.speed, "status": s.status, "reason": wtf(s.reason)}
                              for s in ms.states]
        except PS.DefinitionError as e:
            case["error"] = wtf(str(e))
        except Exception as e:  # the reference stops with an exception it does not handle
            case["crash"] = wtf(f"{type(e).__name__}: {e}")
        out.cases["parse"].append(case)

    def load_bytes(raw, mod, side, clients, name):
        # playerstates.load from the bytes on: decode, byte order mark, parse
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError as e:
            raise PS.DefinitionError(f"mod {mod}: {name}: not UTF-8: {e}") from e
        if text.startswith("\ufeff"):
            raise PS.DefinitionError(f"mod {mod}: {name}: starts with a byte order mark (strict JSON has none)")
        return PS.parse(text, mod, side, clients, source=name)
    p("the format note's example", json.dumps(note), mod="cinder")
    p("a clip written as a GUID", json.dumps({"format": 1, "states": {"a": {"clip": "6DD6A543-1111-2222-3333-444455556666", "like": "X"},
                                                                      "b": {"clip": "6dd6a5431111222233334444555566 66", "like": "X"},
                                                                      "c": {"clip": "6dd6a543-1111-2222-3333-444455556666\n", "like": "X"},
                                                                      "d": {"clip": "{6dd6a543-1111-2222-3333-444455556666}", "like": "X"}}}))
    good = {"clip": "x", "like": "Block_FullBody"}
    bad = {"unknown-key": dict(good, colour=1), "blend-high": dict(good, blendIn=5.01), "blend-low": dict(good, blendIn=-0.1),
           "blend-text": dict(good, blendIn="0.1"), "blend-bool": dict(good, blendIn=True), "loop-text": dict(good, loop="yes"),
           "speed-zero": dict(good, speed=0), "speed-low": dict(good, speed=-1), "speed-huge": dict(good, speed=1e300), "speed-tiny": dict(good, speed=1e-60),
           "no-clip": {"like": "Block_FullBody"}, "empty-clip": dict(good, clip=""), "no-like": {"clip": "x"}, "like-3": dict(good, like="a/b/c"),
           "like-empty-part": dict(good, like="/Block_FullBody"), "not-an-object": [1], "a/b": good, "n" * 65: good}
    states = dict(bad, ok=good, **{"n" * 64: good, "edge": dict(good, blendIn=5, speed=3.4e38, loop=False)})
    p("wrong states are refused and named", json.dumps({"format": 1, "states": states}))
    p("a state twice", '{"format": 1, "states": {"a": {"clip": "x", "like": "L"}, "b": {"clip": "x", "like": "L"}, "a": {"clip": "y", "like": "L"}}}')
    p("a key twice in a state", '{"format": 1, "states": {"a": {"clip": "x", "clip": "y", "like": "L"}}}')
    for i, text in enumerate(["{", '{"format": 1, "states": {},}', '// c\n{"format": 1, "states": {}}', '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": NaN}}}',
                              '{"format": 2, "states": {}}', '{"format": "1", "states": {}}', '{"format": 1.0, "states": {}}', '{"format": 1}', '{"format": 1, "states": []}',
                              '{"format": 1, "states": {}, "mod": "x"}', '{"format": 1, "format": 1, "states": {}}', "[]", '{"format": 1, "states": {}}']):
        p(f"files that cannot be read {i}", text)
    p("a byte order mark", raw=b"\xef\xbb\xbf" + json.dumps(note).encode())
    p("a byte order mark in the text", "\ufeff" + json.dumps(note))
    for i, (side, clients) in enumerate([("server", "mod"), ("both", None), ("both", "kiln"), (None, "mod"), ("client", "mod"), ("both", "mod"), ("server", None)]):
        p(f"side and clients {i}", json.dumps({"format": 1, "states": {"a": {"clip": "x", "like": "Block_FullBody"}, "b": {"clip": "x", "like": "Parry_FullBody"},
                                                                     "bad": {"clip": "x"}}}), side=side, clients=clients)
    p("ids alike in one file", json.dumps({"format": 1, "states": {"a": {"clip": "x", "like": "L"}, "b": {"clip": "x", "like": "L"}, "c": {"clip": "x", "like": "L"}}}),
      hook="state_id_1234")
    # Python's json, rule for rule: where a file is refused and in which words
    jsonish = ['', ' ', '{"format": 1, "states": {}} x', '{"format": 1 "states": {}}', '{"format" 1}', '{format: 1}', '{"format": 1, }', '[1, 2,]', '[1 2]',
               '{"format": 1, "states": {"a": {"clip": "x\u0001", "like": "L"}}}', '{"format": 1, "states": {"a": {"clip": "\\x", "like": "L"}}}',
               '{"format": 1, "states": {"a": {"clip": "\\u12", "like": "L"}}}', '{"format": 1, "states": {"a": {"clip": "\\u12g4yy", "like": "L"}}}',
               '{"format": 1, "states": {"a": {"clip": "x', '"\\', '{"format": 1, "states": {"a": {"clip": "\\ud83d\\ude00\\u00e9\\ud800x\\udc00", "like": "L\\ud800"}}}',
               '{"format": 1, "states": {"a\\ud800": {"clip": "x", "like": "L"}}}', '{"format": 1, "states": {"a": {"clip": "x\\ud800", "like": "L"}}}',
               '{"format": -0, "states": {}}', '{"format": 1e0, "states": {}}', '{"format": 10000000000000000000000000, "states": {}}', '{"format": 01, "states": {}}',
               '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "blendIn": 1e400, "speed": 1e400}}}',
               '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "blendIn": -1e400, "speed": 1e-400}}}',
               '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": 340282356779733661637539395458142568448}}}',
               '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": 340282356779733661637539395458142568447}}}',
               '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": 3.4028235677973366e38, "blendIn": 0.1}}}',
               '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": 1e-45, "blendIn": 4.99999999999999999}}}',
               '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": ' + "9" * 400 + '}}}',
               '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": 1E+2, "blendIn": 5e-0}}}',
               '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": 1.}}}', '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": -}}}',
               '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": 1e+}}}', '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "loop": tru}}}',
               '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": Infinity}}}', '{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": -Infinity}}}',
               '{"format": 1, "states": {"\u00e9\u4e2d\U0001f600": {"clip": "\u00e9", "like": "\u4e2d/\U0001f600"}, "' + "\u00e9" * 64 + '": {"clip": "x", "like": "L"}, "' + "\u00e9" * 65 + '": {"clip": "x", "like": "L"}}}',
               '\n\n  {"format": 1,\n "states": {"a": [1,\n 2,]}}', '{"format": 1, "states": {"a": {"clip": "x", "like": "L/"}, "b": {"clip": "x", "like": "/"}}}',
               '{"format": 1, "states": {"": {"clip": "x", "like": "L"}, "\\u0000": {"clip": "x", "like": "L"}}}', '{"format": [1, {"a": null}], "states": {}}',
               '{"format": {"a": 1, "a": [true, false, null, 1.5, -2e-7, 1e16, 123456789.25]}, "states": {}}', '{"states": {}, "format": 1, "states": 3}']
    for i, text in enumerate(jsonish):
        p(f"json {i}", text)
    for i, raw in enumerate([b'{"format": 1, "states": {}}\xff', b'{"format": 1, "states": {"\xc3": 1}}', b'{"format": 1, "states": {}}\xe2\x82',
                             b'\xed\xa0\x80', b'\xf0\x90\x28', b'\xf0\x9f\x98', b'\xe0\x80\x80', b'\xc0\xaf', b'\xf4\x90\x80\x80', b'\xc3\x28', b'ok\xe2\x28\xa1']):
        p(f"bytes {i}", raw=raw)
    for i, text in enumerate(['{\n  // my mod\n  "id": "cinder", "side": "both", "clients": "mod",\n}\n', '{"id": "m", "side": "both", "clients": "mod"}',
                              '  // a\n\n  // b\n{"id": "m",\n "x": [1, 2 ,\n ], "side": NaN}', '{"id": ""}', '{"side": "both"}', '[1]',
                              '{"id": "m", "id": "n", "clients": 5}', '{"id": "m" "x"}', '{"id": "a,]"}', '{"id": "m"} // trailing', '\u3000// wide\n{"id": "w"}',
                              '{"id": "m", "s": "x\n  // not a comment"}']):
        f = out.dir / f"manifest-{i}" / "kiln.mod.json"
        f.parent.mkdir(exist_ok=True)
        f.write_text(text, encoding="utf-8")
        case = {"text": text}
        try:
            try:
                man = PS.read_manifest(f)
            except (OSError, ValueError) as e:
                raise PS.DefinitionError(f"kiln.mod.json: {e}") from e
            mod = man.get("id")
            if not isinstance(mod, str) or not mod:
                raise PS.DefinitionError('kiln.mod.json: no "id"')
            case["fields"] = {k: (man.get(k) if isinstance(man.get(k), str) else None) for k in ("id", "side", "clients")}
        except PS.DefinitionError as e:
            case["error"] = str(e)
        except Exception as e:
            case["crash"] = f"{type(e).__name__}: {e}"
        out.cases["manifest"].append(case)


def texts(out):
    rng = random.Random(25)
    samples = [b"", b"abc", b"\xff", b"\xc3\xa9", b"\xc3", b"\xe2\x82", b"\xe2\x82\xac", b"\xe0\x80\xaf", b"\xed\xa0\x80", b"\xf0\x9f\x98\x80", b"\xf0\x9f\x98",
               b"\xf4\x90\x80\x80", b"\xf8\x88\x80\x80\x80", b"\x80\xbf", b"a\xe2\x28\xa1b", b"\xc0\xaf", b"\xef\xbf\xbe"]
    for _ in range(300):
        samples.append(bytes(rng.choice([0x41, 0x7f, 0x80, 0xbf, 0xc2, 0xdf, 0xe0, 0xed, 0xef, 0xf0, 0xf4, 0xf5, 0xff, rng.randrange(256)]) for _ in range(rng.randrange(1, 9))))
    out.cases["text"] = [{"bytes": b.hex(), "text": b.decode("utf-8", "replace")} for b in samples]
    strs = ["", "a", "it's", 'say "hi"', "both ' and \"", "back\\slash", "tab\tnl\ncr\r", "\x00\x1f\x7f", "\x80\x9f\xa0\xad\xe9\xff", "\u0100\u4e2d\u2028\u2029\u200b",
            "\ufeff\ufffd\uffff\U0001f600\U000e0001\U00010000", "\ue000\U000f0000\U00100000", "\u1680\u3000\u00a0\u0085", "\U0001d173\U00013430", "\ud800 lone \udfff"]
    out.cases["repr"] = [{"s": s.encode("utf-8", "surrogatepass").hex(), "repr": repr(s)} for s in strs]
    # unassigned code points: Python escapes them, by its own Unicode version; the port keeps all but the empty planes and the
    # noncharacters (a known difference, reported, not failed)
    out.cases["repr_unassigned"] = [{"s": s.encode("utf-8").hex(), "repr": repr(s)} for s in ["\u0378", "\u0530 \u2fff", "\U0001fffd"]]
    floats = [0.0, -0.0, 1.0, 0.1, 0.12, 1.25, 5.01, -0.1, 1e300, 1e-60, 3.4e38, 1e16, 1e15, 123456789012345678.0, 1234567890123456.0, 1e-4, 1e-5, 0.0001234,
              1.5e-7, 2.5, 100.0, 1e22, float("inf"), float("-inf"), float("nan"), 5e-324, 1.7976931348623157e308]
    out.cases["floatrepr"] = [{"x": x.hex() if x == x else "nan", "repr": repr(x)} for x in floats]


def graphgen_graphs(out):
    class Sk:
        guid = "00000000-0000-0000-0000-00000000000a"
        index = {"root": 0, "hand_r": 5, "arm_r": 4, "spine": 1}

    def clips(names, dur=1.0):
        return {n: {"guid": G.derive("m", "clip", n), "duration": dur, "loop": True, "rootMotion": False, "events": False} for n in names}
    cl = clips(["a", "b", "c", "d", "e"])
    specs = {
        "graphgen flat": {"states": {"idle": {"engine": "Idle", "clip": "a"}, "slam": {"clip": "b", "blendIn": 0.1}}, "slots": {"HandR": "hand_r"}},
        "graphgen nested": {"states": dict({f"s{i}": {"clip": "a"} for i in range(40)}, idle={"engine": "Idle", "clip": "b"}), "entry": "idle"},
        "graphgen chains": {"states": {"a": {"clip": "a", "loop": False, "then": "b"}, "b": {"clip": "b", "loop": False, "then": "c"}, "c": {"clip": "c"},
                                       "d": {"clip": "d"}}},
        "graphgen blend": {"states": {"walk": {"engine": "Walk", "blend": {"input": "moveDirection", "clips": {"90": "a", "-90": "b", "0": "c"}}}}},
        "graphgen overlay and mask": {"states": {"i": {"clip": "a", "speed": 1.5}}, "layers": [{"name": "f", "overlay": "hit", "clip": "b", "reference": "c",
                                                                                               "weight": 0.5, "bones": ["arm_r"]}]},
        "graphgen override layer": {"states": {"i": {"clip": "a"}}, "layers": [{"name": "up", "mode": "override", "clip": "b", "bones": {"spine": 0.5}}]},
    }
    for name, spec in specs.items():
        v, _ = G.Compiler("m", "g", spec, Sk(), cl).compile()
        raw = encode(v)
        out.cases["codec"].append({"name": name, "graph": out.graph(name.replace(" ", "-"), raw)})


def joint_animations(out):
    def ja(types=TYPES, **kw):
        v = {"hierarchyHash": TP.HIER, "positionFactor": 1.0, "positionTangent": 1.0, "scaleFactor": 1.0, "headers": [], "data": [], "fps": 30, "frameCount": 45,
             "jointCount": TP.JOINTS, "flags": 1}
        v.update(kw)
        return Encoder(types).encode(types.get(PG.JOINT_ANIM), v)

    def case(name, raw, types=TYPES, types_name=None):
        c = {"name": name, "bytes": raw.hex(), "types": types_name}
        try:
            h = PG.ClipFacts(types)._header(raw)
            c["facts"] = {"hierarchyHash": h["hierarchyHash"], "jointCount": h["jointCount"], "fps": h["fps"], "frameCount": h["frameCount"], "loop": h["loop"]}
        except PG.MergeOff as e:
            c["error"] = f"MergeOff: {e}"
        except Exception as e:
            c["error"] = f"{type(e).__name__}: {e}"
        out.cases["jointanim"].append(c)
    case("plain", ja())
    case("no loop, 0xFFFF", ja(fps=0xFFFF, frameCount=0xFFFF, flags=0xFFFE, hierarchyHash=0xFFFFFFFFFFFFFFFF))
    case("short", ja()[:40])
    dump = json.loads(json.dumps(TYPES.dump))
    t = next(x for x in dump["types"] if x["name"] == PG.JOINT_ANIM)
    for f in t["fields"]:
        if f["name"] == "fps":
            f["name"] = "framesPerSecond"
    case("a field renamed", ja(), TypeDb(dump), out.types("ja-renamed", TypeDb(dump)))
    dump = json.loads(json.dumps(TYPES.dump))
    t = next(x for x in dump["types"] if x["name"] == PG.JOINT_ANIM)
    for f in t["fields"]:
        if f["name"] == "fps":
            f["type"] = "keen::float32"
    case("a float field", ja(), TypeDb(dump), out.types("ja-float", TypeDb(dump)))


def fuzz(out, raw, facts, n=160):
    """Bytes changed at random: the reference's verdicts (does it decode, does it round-trip, the merge's report)."""
    rng = random.Random(2025)
    st = parse(mod_input({"a": {"clip": "x", "like": "Parry_FullBody"}}))
    for i in range(n):
        b = bytearray(raw)
        for _ in range(rng.choice([1, 1, 2, 4])):
            at = rng.randrange(len(b))
            b[at] = rng.choice([0, 0xFF, b[at] ^ (1 << rng.randrange(8)), rng.randrange(256)])
        if rng.random() < 0.1:
            b = b[:rng.randrange(len(b))]
        b = bytes(b)
        c = {"graph": out.graph(f"fuzz-{i}", b)}
        try:
            v = decode(b)
            c["decodes"] = True
            try:
                c["roundtrip"] = encode(v) == b
            except Exception as e:
                c["roundtrip"] = False
                c["encode_error"] = type(e).__name__
        except Exception as e:
            c["decodes"] = False
            c["decode_error"] = type(e).__name__
        try:
            res, rep = PG.merge(b, TYPES, [st], facts.get)
            c["report"] = rep
            c["bin"] = out.file(f"fuzz-want-{i}.bin", res) if res is not None else None
        except Exception as e:  # the reference stops: no verdict to compare
            c["crash"] = f"{type(e).__name__}: {e}"
        out.cases["fuzz"].append(c)
    out.cases["fuzz_facts"] = facts_json(facts)


def game(out, graph_path, game_dir):
    """game_playergraph_check.py's scenarios on the real graph."""
    sys.path.insert(0, str(REPO / "tools" / "animcook" / "tests"))
    import game_playergraph_check as GC
    raw = Path(graph_path).read_bytes()
    k = None
    if game_dir and Path(game_dir).is_dir() and any(Path(game_dir).glob("*.kfc")):
        from kfc.archive import Kfc
        k = Kfc(Path(game_dir))
    types = TYPES
    value = decode(raw, types)
    base = PG.ClipFacts(types, [], k) if k else GC.graph_facts(value).get
    seen = {}

    def facts(gd):
        f = base(gd)
        seen[gd] = f
        return f
    g = PG.Graph(decode(raw, types))
    nbs = GC.neighbours(g)
    states = {f"n{i:02d}": {"clip": nb.clip["animation"], "like": name} for i, (name, _, _, nb) in enumerate(nbs) if not nb.error}
    names = list(states)
    hammer = next(name for name, _, m, nb in nbs if m["debugName"] == "SM_Attack_Hammer_FullBody" and not nb.error)
    hn = next(nb for name, _, _, nb in nbs if name == hammer)
    player = facts(hn.clip["animation"])
    other = GC.other_skeleton_clip(k, types, facts, player["hierarchyHash"]) if k else None
    if other is None and not k:
        other = "0f0f0f0f-0000-4000-8000-000000000001"
        seen[other] = dict(player, hierarchyHash=player["hierarchyHash"] + 1)
    bad = {"unknown": {"clip": hn.clip["animation"], "like": "No_Such_State_Anywhere"}, "ambiguous": {"clip": hn.clip["animation"], "like": "Attack_Heavy"},
           "idle-block": {"clip": hn.clip["animation"], "like": "Idle_Block"}, "skeleton": {"clip": other or PG.ZERO, "like": hammer},
           "good-1": {"clip": hn.clip["animation"], "like": hammer}, "good-2": {"clip": hn.clip["animation"], "like": "SM_Main/Parry_FullBody"}}
    scen = [("no mod", []), ("a mod with no states", [mod_input({}, "pa-empty")]), ("one beside every neighbour", [mod_input(states, "pa-all")]),
            ("two mods", [mod_input({n: states[n] for n in names[::2]}, "pa-left"), mod_input({n: states[n] for n in names[1::2]}, "pa-right")]),
            ("two mods, the other order", [mod_input({n: states[n] for n in names[1::2]}, "pa-right"), mod_input({n: states[n] for n in names[::2]}, "pa-left")]),
            ("thirty beside a hammer state", [mod_input({f"h{i:02d}": {"clip": hn.clip["animation"], "like": hammer} for i in range(30)}, "pa-thirty")]),
            ("refusals", [mod_input(bad, "pa-bad")])]
    for _, mods in scen:  # every GUID the merges ask about, so both sides see the same facts
        PG.merge(raw, types, [parse(m) for m in mods], lambda gd: seen[gd] if gd in seen else facts(gd))
    cases = []
    for name, mods in scen:
        res, rep = PG.merge(raw, types, [parse(m) for m in mods], seen.get)
        cases.append({"name": name, "mods": mods, "report": rep, "bin": out.file(f"game-want-{len(cases)}.bin", res) if res is not None else None})
    out.cases["game"] = {"graph": str(Path(graph_path).resolve()), "bytes": len(raw), "facts_from": "the installed game" if k else "the graph's own clip nodes",
                         "facts": facts_json(seen), "neighbours": len(nbs), "cases": cases}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True)
    ap.add_argument("--graph")
    ap.add_argument("--game")
    a = ap.parse_args()
    out = Out(Path(a.out))
    _, raw, facts = rules(out)
    definitions(out)
    texts(out)
    graphgen_graphs(out)
    joint_animations(out)
    fuzz(out, raw, facts)
    if a.graph and Path(a.graph).is_file():
        game(out, a.graph, a.game)
        print(f"cases: the game's graph {a.graph}, facts from {out.cases['game']['facts_from']}")
    else:
        print(f"cases: SKIP the game's graph: {'no file at ' + a.graph if a.graph else 'none given'} (KILN_PLAYER_GRAPH)")
    (out.dir / "cases.json").write_text(json.dumps(out.cases, ensure_ascii=True))
    print(f"cases: {len(out.cases['codec'])} codec, {len(out.cases['parse'])} definition, {len(out.cases['merge'])} merge, {len(out.cases['fuzz'])} fuzz -> {out.dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
