# SPDX-License-Identifier: MIT
"""Player states (anicook/playerstates.py) and their merge into the player's graph (anicook/playergraph.py), without game files: a small
player-like graph written with kfc.animgraph.Nodes and the bundled reflection, the merge run on its bytes, the result decoded and checked
rule by rule. `python3 -m unittest discover -s tools/animcook/tests`. Nothing is written outside a temp folder."""
import json
import random
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path[:0] = [str(ROOT), str(ROOT.parent / "kfc"), str(ROOT.parent / "modelcook")]

from anicook import graphgen as G, playergraph as PG, playerstates as PS  # noqa: E402
from kfc import animgraph as A  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402
from kfc.util import fnv1a32  # noqa: E402

TYPES = TypeDb.load(ROOT / "types" / "anim-types.json")
GRAPH = PG.GRAPH_TYPE
SI, FAMILY = A.STATE_INPUT, 0x6287FE46
HIER, JOINTS = 0x1122334455667788, 102
ZERO = A.ZERO


def sid(name):
    """The id of a state of the synthetic game."""
    return A.fnv_guid(G.derive("game", "state", name))


def cg(name):
    return G.derive("game", "clip", name)


def mclip(name, mod="m"):
    return G.derive(mod, "clip", name)


def facts(fps=60, frames=90, loop=False, rm=True, seq=True, hier=HIER, joints=JOINTS):
    return {"hierarchyHash": hier, "jointCount": joints, "fps": fps, "frameCount": frames, "loop": loop, "rootMotion": rm, "sequences": seq}


class Synth:
    """A player-like graph: SM_Main with the six states of group 1 (a layer blend over a cached pose, a list-entered state with local ways in,
    a select on the state input inside a state's own pose, a layer blend over a clip), a melee wrapper machine holding the unarmed (local
    transitions only), sword, hammer (single ids) and dagger machines, each listed in a wrapper list, in a list under SM_Main and in a
    "reset" list of SM_UpperBodyOverride, a jump attack machine entered by lists, a select on the state input outside any neighbour, and the
    block legs SM_Block_Locomotion. `hammer`: how many plain states the hammer machine has (it has two more)."""

    def __init__(self, hammer=4):
        n = self.n = A.Nodes("game")
        self.facts = {}
        self.hammer = [f"Attack_Hammer_{i:02d}" for i in range(hammer)]
        # the block legs, cached
        s_ib = n.state("s.ib", "Idle_Block", n.pose("p.ib", self.clip("idle_block", loop=True)), "sm.bl")
        s_mb = n.state("s.mb", "Move_Block", n.pose("p.mb", self.clip("move_block", loop=True)), "sm.bl")
        t_bl = n.transition("t.bl", s_ib, s_mb, 0.2, 0, auto=True)
        m_bl = n.machine("sm.bl", "SM_Block_Locomotion", [s_ib, s_ib, s_mb], s_ib, transitions=[t_bl])
        save_bl = self.save("cache.bl", n.pose("p.bl", m_bl), "BlockLocomotion")
        melee_ids, wrappers = [], []
        # unarmed: local transitions only, and a state with no way in
        b = n._add("BoolParameter", "bool.u1", parameterHash={"value": 0x1234})
        s_u1 = n.state("s.u1", "Attack_Unarmed_01", n.pose("p.u1", self.sel_bool("sel.u1", self.clip("u1a"), self.clip("u1b"), b)), "sm.un", retrigger=0.15)
        s_u2 = n.state("s.u2", "Attack_Unarmed_02", n.pose("p.u2", self.clip("u2")), "sm.un", retrigger=0.15)
        s_u3 = n.state("s.u3", "Attack_Unarmed_Lonely", n.pose("p.u3", self.clip("u3")), "sm.un")
        self.edit(n.nid("clip.u1b"), preventRootMotion=True, loopMode="Loop")  # the second input differs from the first
        t12 = n.transition("t.u12", s_u1, s_u2, 0.15, n.id_equals("c.u12", SI, sid("Attack_Unarmed_02")))
        self.t21 = n.transition("t.u21", s_u2, s_u1, 0.15, n.id_equals("c.u21", SI, sid("Attack_Unarmed_01")))
        self.edit(self.t21, rootMotionMode="UseRootMotionFromDestinationState")
        m_un = n.machine("sm.un", "SM_Attack_Unarmed_FullBody", [s_u1, s_u1, s_u2, s_u3], s_u1, parent_state=n.nid("w.un"), transitions=[t12, self.t21])
        wrappers.append(self.wrapper("un", m_un, "Attack_Unarmed_FullBody", ["Attack_Unarmed_01", "Attack_Unarmed_02"]))
        melee_ids += ["Attack_Unarmed_01", "Attack_Unarmed_02"]
        # sword: global transitions, one local transition; Attack_Heavy here and in the daggers
        sword = ["Attack_Sword_Shield_01", "Attack_Sword_Shield_02", "Attack_Heavy"]
        st = {nm: n.state(f"s.{nm}.sw", nm, n.pose(f"p.{nm}.sw", self.clip(f"{nm}.sw")), "sm.sw") for nm in sword}
        gts = [(st[nm], 0.15, n.id_equals(f"c.{nm}.sw", SI, sid(nm + ".sw"))) for nm in sword]
        t_s12 = n.transition("t.s12", st[sword[0]], st[sword[1]], 0.25, n.id_equals("c.s12", SI, sid(sword[1] + ".sw")))
        m_sw = n.machine("sm.sw", "SM_Attack_Sword_Shield_FullBody", [st[sword[0]]] + [st[x] for x in sword], st[sword[0]], parent_state=n.nid("w.sw"),
                         global_transitions=gts, transitions=[t_s12])
        wrappers.append(self.wrapper("sw", m_sw, "Attack_Sword_Shield_FullBody", [x + ".sw" for x in sword]))
        melee_ids += [x + ".sw" for x in sword]
        # hammer: single ids; an additive clip, a blend space, a clip with gender alternatives and a field of its own
        hs = {}
        for nm in self.hammer:
            hs[nm] = n.state(f"s.{nm}", nm, n.pose(f"p.{nm}", self.clip(nm)), "sm.ha", retrigger=0.3)
        self.edit(n.nid(f"clip.{self.hammer[0]}"), animationGenderAlternative=cg("female"), animationGenderAlternativeHash={"value": A.fnv_guid(cg("female"))},
                  useRootMotionRotation=True)
        self.facts[cg("ref")] = facts()
        hs["Attack_Hammer_Additive"] = n.state("s.ha.add", "Attack_Hammer_Additive", n.pose("p.ha.add", self.clip("ha.add", additive=cg("ref"))), "sm.ha")
        blend = n.blend("blend.ha", 0x28BB2FF6, [(0.0, cg("b0"), 1.0, False), (1.0, cg("b1"), 1.0, False)])
        hs["Attack_Hammer_Blend"] = n.state("s.ha.blend", "Attack_Hammer_Blend", n.pose("p.ha.blend", blend), "sm.ha")
        names = list(hs)
        gts = [(hs[nm], 0.3, n.id_equals(f"c.{nm}", SI, sid(nm))) for nm in names]
        m_ha = n.machine("sm.ha", "SM_Attack_Hammer_FullBody", [hs[names[0]]] + [hs[x] for x in names], hs[names[0]], parent_state=n.nid("w.ha"),
                         global_transitions=gts)
        for e in self.value_of(m_ha)["globalTransitions"][:len(gts)]:
            e["rootMotionMode"] = "UseRootMotionFromHighestWeight"
        wrappers.append(self.wrapper("ha", m_ha, "Attack_Hammer_FullBody", names))
        melee_ids += names
        # daggers
        s_dh = n.state("s.dh", "Attack_Heavy", n.pose("p.dh", self.clip("dh")), "sm.da")
        m_da = n.machine("sm.da", "SM_Attack_Dual_Daggers_FullBody", [s_dh, s_dh], s_dh, parent_state=n.nid("w.da"),
                         global_transitions=[(s_dh, 0.2, n.id_equals("c.dh", SI, sid("Attack_Heavy.da")))])
        wrappers.append(self.wrapper("da", m_da, "Attack_Dual_Daggers_FullBody", ["Attack_Heavy.da"]))
        melee_ids += ["Attack_Heavy.da"]
        m_melee = n.machine("sm.melee", "SM_Melee_Attack_FullBody", [wrappers[0][0]] + [w for w, _ in wrappers], wrappers[0][0], parent_state=n.nid("s.melee"),
                            global_transitions=[(w, 0.1, lst) for w, lst in wrappers])
        p_melee = n.pose("p.melee", m_melee)
        # jump attack: each state entered by a list of two ids (one per weapon family), the clip picked by family
        jump = ["Jump_Attack_Start_FullBody", "Jump_Attack_End_FullBody", "Jump_Attack_Loop_FullBody"]
        js, jgts, self.jump_ids = {}, [], []
        for nm in jump:
            sel = self.sel_id(f"sel.{nm}", FAMILY, [self.clip(nm + ".a"), self.clip(nm + ".b")], [0xA, 0xB])
            js[nm] = n.state(f"s.{nm}", nm, n.pose(f"p.{nm}", sel), "sm.jump", retrigger=0.3)
            ids = [sid(nm + ".a"), sid(nm + ".b")]
            self.jump_ids += ids
            jgts.append((js[nm], 0.15, n.id_equals_any(f"l.{nm}", SI, ids)))
        m_jump = n.machine("sm.jump", "SM_Jump_Attack_FullBody", [js[jump[0]]] + [js[x] for x in jump], js[jump[0]], parent_state=n.nid("s.jump"), global_transitions=jgts)
        p_jump = n.pose("p.jump", m_jump)
        # SM_Main
        main = {}
        main["Grounded"] = n.state("s.gr", "GroundedLocomotion_FullBody", n.pose("p.gr", self.clip("gr", loop=True)), "sm.main")
        use = n._add("UseCachedPose", "use.bl", cachedPose={"value": save_bl}, cachedPoseDebugName="BlockLocomotion")
        guard = self.sel_id("sel.guard", FAMILY, [self.clip("guard.a"), self.clip("guard.b")], [0xA, 0xB])
        self.mask = G.derive("game", "mask", "upper")
        lb = n.layer("lb.block", use, guard, additive=False, bone_mask=self.mask, weight=0.75)
        main["Block_FullBody"] = n.state("s.block", "Block_FullBody", n.pose("p.block", lb), "sm.main")
        main["Parry_FullBody"] = n.state("s.parry", "Parry_FullBody", n.pose("p.parry", self.clip("parry")), "sm.main", retrigger=0.12)
        main["DodgeRoll_FullBody"] = n.state("s.dr", "DodgeRoll_FullBody", n.pose("p.dr", self.clip("dr")), "sm.main", retrigger=0.1)
        main["Dodge_Attack_FullBody"] = n.state("s.da", "Dodge_Attack_FullBody", n.pose("p.da", self.clip("da")), "sm.main")
        sneak = self.sel_id("sel.sneak", SI, [self.clip("sa.a"), self.clip("sa.b")], [sid("SA.a"), sid("SA.b")])
        main["Sneak_Attack_FullBody"] = n.state("s.sa", "Sneak_Attack_FullBody", n.pose("p.sa", sneak), "sm.main")
        kick = n.layer("lb.kick", self.clip("kick.base"), self.clip("kick.layer"))
        main["Attack_Skill_Kick_FullBody"] = n.state("s.kick", "Attack_Skill_Kick_FullBody", n.pose("p.kick", kick), "sm.main")
        main["Melee"] = n.state("s.melee", "Melee_Attack_FullBody", p_melee, "sm.main", restart=False)
        main["Jump"] = n.state("s.jump", "Jump_Attack_FullBody", p_jump, "sm.main", restart=False)
        self.main_melee_list = n.id_equals_any("l.main.melee", SI, [sid(x) for x in melee_ids])
        self.main_jump_list = n.id_equals_any("l.main.jump", SI, self.jump_ids)
        mg = [(main["Grounded"], 0.3, n.id_equals_any("l.gr", SI, [sid("Idle")])),
              (main["Block_FullBody"], 0.1, n.id_equals("c.block", SI, sid("Block"))),
              (main["Parry_FullBody"], 0.12, n.id_equals("c.parry", SI, sid("Parry"))),
              (main["DodgeRoll_FullBody"], 0.1, n.id_equals("c.dr", SI, sid("Dodge"))),
              (main["Dodge_Attack_FullBody"], 0.2, n.id_equals_any("l.da", SI, [sid("DA.a"), sid("DA.b")])),
              (main["Sneak_Attack_FullBody"], 0.2, n.id_equals_any("l.sa", SI, [sid("SA.a"), sid("SA.b")])),
              (main["Attack_Skill_Kick_FullBody"], 0.2, n.id_equals("c.kick", SI, sid("Kick"))),
              (main["Melee"], 0.1, self.main_melee_list), (main["Jump"], 0.1, self.main_jump_list)]
        self.t_dr_da = n.transition("t.dr.da", main["DodgeRoll_FullBody"], main["Dodge_Attack_FullBody"], 0.05,
                                    n.id_equals_any("l.dr.da", SI, [sid("DA.a"), sid("DA.b")]))
        self.t_dr_j = n.transition("t.dr.j", main["DodgeRoll_FullBody"], main["Jump"], 0.1, n.id_equals_any("l.dr.j", SI, self.jump_ids))
        self.t_pa_da = n.transition("t.pa.da", main["Parry_FullBody"], main["Dodge_Attack_FullBody"], 0.1, n.id_equals("c.pa.da", SI, sid("Other")))
        m_main = n.machine("sm.main", "SM_Main", [main["Grounded"]] + list(main.values()), main["Grounded"], global_transitions=mg,
                           transitions=[self.t_dr_da, self.t_dr_j, self.t_pa_da])
        p_main = n.pose("p.main", m_main)
        # SM_UpperBodyOverride: the reset list, a select on the state input outside every neighbour, a list holding part of an entry
        attack_ids = [sid(x) for x in melee_ids] + self.jump_ids + [sid(x) for x in ("Block", "Parry", "Dodge", "DA.a", "DA.b", "SA.a", "SA.b", "Kick")]
        s_dn = n.state("s.dn", "DoNothing", n.pose("p.dn", n.empty_additive("empty.dn")), "sm.ub")
        self.ov_pose = n.pose("p.ov.h", self.clip("ov.h"))
        ov = self.sel_id("sel.ov", SI, [self.ov_pose, n.pose("p.ov.s", self.clip("ov.s"))], [sid(self.hammer[0]), sid("Attack_Sword_Shield_01.sw")])
        s_ov = n.state("s.ov", "Overlay_Pose", n.pose("p.ov", ov), "sm.ub")
        s_part = n.state("s.part", "Partial", n.pose("p.part", self.clip("part")), "sm.ub")
        self.reset = n.id_equals_any("l.reset", SI, attack_ids)
        self.partial = n.id_equals_any("l.part", SI, [self.jump_ids[0]])
        m_ub = n.machine("sm.ub", "SM_UpperBodyOverride", [s_dn, s_dn, s_ov, s_part], s_dn,
                         global_transitions=[(s_dn, 0.2, self.reset), (s_ov, 0.2, n.id_equals("c.ov", SI, sid("Overlay"))), (s_part, 0.2, self.partial)])
        root = n.pose("root", n.layer("lb.root", p_main, n.pose("p.ub", m_ub), additive=False))
        compared = set()
        for x in n.nodes:
            v = x["value"]
            if v["type"] == "IdParameterEqualsConstant" and v["parameterHash"]["value"] == SI:
                compared.add(v["value"]["value"])
            if v["type"] == "IdParameterEqualsConstantList":
                compared.update(y["value"] for y in v["values"])
        self.value = A.graph_value(n, G.derive("game", "skeleton", "player"), root, ids=(FAMILY, SI), used=sorted(compared) + [sid("Idle")])
        gtt = TYPES.get(PG.GT_TYPE)
        blank = Decoder(TYPES).decode(gtt, b"\0" * gtt["size"])
        for x in n.nodes:  # the game's unused entries are zero bytes (kfc.animgraph writes another eventMode in them)
            v = x["value"]
            if v["type"] == "StateMachine":
                v["globalTransitions"][v["numGlobalTransitions"]:] = [dict(blank) for _ in range(A.MAX_GLOBAL - v["numGlobalTransitions"])]
        G.check_fields(TYPES, self.value)

    # helpers writing nodes of the shape the game's converter writes
    def clip(self, name, loop=False, additive=None):
        self.facts[cg(name)] = facts(loop=loop)
        return self.n.clip(f"clip.{name}", cg(name), 1.5, loop=loop, root_motion=True, has_sequences=True, additive=additive)

    def value_of(self, nid):
        return next(x["value"] for x in self.n.nodes if x["value"]["id"]["value"] == nid)

    def edit(self, nid, **fields):
        self.value_of(nid).update(fields)

    def save(self, name, pose, debug):
        return self.n._add("SaveCachedPose", name, inputPose={"value": pose}, debugName=debug, freezePoseAfterFirstEvaluation=False,
                           silentEvaluateWhileFrozen=False, preventFromRest=False)

    def sel_bool(self, name, a, b, sel):
        return self.n._add("SelectPoseByBool", name, inputA={"value": a}, inputB={"value": b}, selectA={"value": sel}, blendTimeA=0.2, blendTimeB=0.2,
                           syncInputs=False, keepInputValueFixed=False, blendCurve="Linear", blendProfile=ZERO)

    def sel_id(self, name, param, poses, values):
        return self.n._add("SelectPoseByParameterId", name, parameterHash={"value": param}, inputPoses=[{"value": p} for p in poses],
                           values=[{"value": v} for v in values], blendTime=0.2, syncInputs=False, keepInputValueFixed=False, blendCurve="Linear",
                           blendProfile=ZERO)

    def wrapper(self, key, machine, debug, ids):
        n = self.n
        w = n.state(f"w.{key}", debug, n.pose(f"p.w.{key}", machine), "sm.melee", restart=False)
        lst = n.id_equals_any(f"l.w.{key}", SI, [sid(x) for x in ids])
        return w, lst

    def raw(self):
        return Encoder(TYPES).encode(TYPES.get(GRAPH), self.value)


def decode(raw):
    return Decoder(TYPES, max_array=1 << 26).decode(TYPES.get(GRAPH), raw)


def mod(states, name="m", side="both", clients="mod"):
    return PS.parse(json.dumps({"format": 1, "states": states}), name, side, clients)


class Base(unittest.TestCase):
    MODS = ("m", "alpha", "beta", "other", "c-mod", "srv")

    @classmethod
    def setUpClass(cls):
        cls.synth = Synth()
        cls.raw = cls.synth.raw()
        cls.pristine = decode(cls.raw)

    def setUp(self):
        self.facts = dict(self.synth.facts)
        for m in self.MODS:
            for nm in ("x", "y", "z", "slash", "guard"):
                self.facts[mclip(nm, m)] = facts(fps=30, frames=47, loop=True, rm=False, seq=False)

    def merge(self, *mods, raw=None, facts=None, types=TYPES):
        return PG.merge(self.raw if raw is None else raw, types, list(mods), facts if callable(facts) else (facts or self.facts).get)

    def merged(self, states, **kw):
        out, rep = self.merge(mod(states), **kw)
        self.assertEqual(rep["graph"], "merged", rep)
        return decode(out), rep

    def by_id(self, v):
        return {n["value"]["id"]["value"]: n["value"] for n in v["nodeDefinitions"]}

    def node(self, v, mod_, state, role):
        return self.by_id(v)[PG.node_id(mod_, state, role)]

    def game(self, name):
        """A node of the synthetic game's graph as decoded (floats are float32)."""
        return self.by_id(self.pristine)[self.synth.n.nid(name)]

    def machine(self, v, debug):
        return next(n["value"] for n in v["nodeDefinitions"] if n["value"]["type"] == "StateMachine" and n["value"]["debugName"] == debug)

    def status(self, rep, name, mod_="m"):
        return next(s for s in rep["states"] if s["name"] == name and s["mod"] == mod_)

    def refused(self, rep, name, word, mod_="m"):
        s = self.status(rep, name, mod_)
        self.assertEqual(s["status"], "refused", s)
        self.assertIn(word, s["reason"])
        self.assertIn(f"mod {mod_}", s["reason"])
        self.assertIn(name, s["reason"])


class Definitions(unittest.TestCase):
    NOTE = {"format": 1, "states": {
        "cinder-slash-1": {"clip": "cinder_slash_1", "like": "Attack_Sword_Shield_01"},
        "cinder-slash-2": {"clip": "cinder_slash_2", "like": "Attack_Sword_Shield_02", "blendIn": 0.12},
        "cinder-uppercut": {"clip": "cinder_uppercut", "like": "SM_Attack_Sword_Shield_FullBody/Attack_Jump_Uppercut", "speed": 1.1},
        "cinder-guard": {"clip": "cinder_guard", "like": "Block_FullBody", "loop": True}}}

    def folder(self, manifest_text, states_text):
        d = Path(tempfile.mkdtemp(dir=self.tmp.name))
        (d / "kiln.mod.json").write_text(manifest_text)
        (d / "assets").mkdir()
        (d / "assets" / PS.FILE_NAME).write_text(states_text)
        return d / "assets" / PS.FILE_NAME

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.tmp.cleanup()

    def test_the_format_notes_example(self):
        man = '{\n  // my mod\n  "id": "cinder", "side": "both", "clients": "mod",\n}\n'
        m = PS.load(self.folder(man, json.dumps(self.NOTE)))
        self.assertEqual((m.mod, m.side, m.clients), ("cinder", "both", "mod"))
        by = {s.name: s for s in m.states}
        self.assertEqual(list(by), list(self.NOTE["states"]))
        self.assertTrue(all(s.status is None for s in m.states), [s.reason for s in m.states])
        s = by["cinder-slash-2"]
        self.assertEqual(s.guid, G.derive("cinder", "state", "cinder-slash-2"))
        self.assertEqual(s.id, A.fnv_guid(s.guid))
        self.assertEqual(s.clip, G.derive("cinder", "clip", "cinder_slash_2"))
        self.assertEqual(s.blend_in, PS.f32(0.12))
        self.assertNotEqual(s.blend_in, 0.12, "stored as the float32 the graph holds")
        self.assertEqual(by["cinder-uppercut"].speed, PS.f32(1.1))
        self.assertIs(by["cinder-guard"].loop, True)
        self.assertIsNone(by["cinder-guard"].blend_in)

    def test_a_clip_written_as_a_guid(self):
        g = "6DD6A543-1111-2222-3333-444455556666"
        m = mod({"a": {"clip": g, "like": "X"}, "b": {"clip": "6dd6a5431111222233334444555566 66", "like": "X"}})
        self.assertEqual(m.states[0].clip, g.lower())
        self.assertEqual(m.states[1].clip, G.derive("m", "clip", "6dd6a5431111222233334444555566 66"), "only the 8-4-4-4-12 form is a GUID")

    def test_wrong_states_are_refused_and_named(self):
        good = {"clip": "x", "like": "Block_FullBody"}
        bad = {"unknown-key": dict(good, colour=1), "blend-high": dict(good, blendIn=5.01), "blend-low": dict(good, blendIn=-0.1),
               "blend-text": dict(good, blendIn="0.1"), "blend-bool": dict(good, blendIn=True), "loop-text": dict(good, loop="yes"),
               "speed-zero": dict(good, speed=0), "speed-low": dict(good, speed=-1), "speed-huge": dict(good, speed=1e300), "speed-tiny": dict(good, speed=1e-60),
               "no-clip": {"like": "Block_FullBody"}, "empty-clip": dict(good, clip=""), "no-like": {"clip": "x"}, "like-3": dict(good, like="a/b/c"),
               "like-empty-part": dict(good, like="/Block_FullBody"), "not-an-object": [1], "a/b": good, "n" * 65: good}
        states = dict(bad, ok=good, **{"n" * 64: good, "edge": dict(good, blendIn=5, speed=3.4e38, loop=False)})
        m = mod(states)
        by = {s.name: s for s in m.states}
        for name in bad:
            with self.subTest(name=name):
                self.assertEqual(by[name].status, "refused")
                self.assertIn(f"mod m, state {name}: ", by[name].reason)
        for name in ("ok", "n" * 64, "edge"):
            self.assertIsNone(by[name].status, by[name].reason)
        self.assertIn("blendIn", by["blend-high"].reason)
        self.assertIn("colour", by["unknown-key"].reason)

    def test_a_state_twice(self):
        text = '{"format": 1, "states": {"a": {"clip": "x", "like": "L"}, "b": {"clip": "x", "like": "L"}, "a": {"clip": "y", "like": "L"}}}'
        m = PS.parse(text, "m", "both", "mod")
        self.assertEqual([s.name for s in m.states], ["a", "b"])
        self.assertIn("mod m, state a: defined twice", m.states[0].reason)
        self.assertIsNone(m.states[1].status)
        m = PS.parse('{"format": 1, "states": {"a": {"clip": "x", "clip": "y", "like": "L"}}}', "m", "both", "mod")
        self.assertIn("clip given twice", m.states[0].reason)

    def test_files_that_cannot_be_read(self):
        for text, word in [("{", "strict JSON"), ('{"format": 1, "states": {},}', "strict JSON"), ('// c\n{"format": 1, "states": {}}', "strict JSON"),
                           ('{"format": 1, "states": {"a": {"clip": "x", "like": "L", "speed": NaN}}}', "NaN"), ('{"format": 2, "states": {}}', "format"),
                           ('{"format": "1", "states": {}}', "format"), ('{"format": 1.0, "states": {}}', "format"), ('{"format": 1}', "states"),
                           ('{"format": 1, "states": []}', "states"), ('{"format": 1, "states": {}, "mod": "x"}', "unknown keys mod"),
                           ('{"format": 1, "format": 1, "states": {}}', "given twice"), ("[]", "an object")]:
            with self.subTest(text=text):
                with self.assertRaises(PS.DefinitionError) as e:
                    PS.parse(text, "m", "both", "mod")
                self.assertIn(word, str(e.exception))
                self.assertIn("mod m", str(e.exception))
        p = self.folder('{"id": "m", "side": "both", "clients": "mod"}', "")
        p.write_bytes(b"\xef\xbb\xbf" + json.dumps(self.NOTE).encode())
        with self.assertRaises(PS.DefinitionError) as e:
            PS.load(p)
        self.assertIn("byte order mark", str(e.exception))
        self.assertEqual(PS.parse('{"format": 1, "states": {}}', "m", "both", "mod").states, [])

    def test_side_and_clients_must_be_written(self):
        text = json.dumps({"format": 1, "states": {"a": {"clip": "x", "like": "Block_FullBody"}, "b": {"clip": "x", "like": "Parry_FullBody"}}})
        for man, ok in [('{"id": "m", "side": "server", "clients": "mod"}', False), ('{"id": "m", "side": "both"}', False),
                        ('{"id": "m", "side": "both", "clients": "kiln"}', False), ('{"id": "m", "clients": "mod"}', False),
                        ('{"id": "m", "side": "client", "clients": "mod"}', False), ('{"id": "m", "side": "both", "clients": "mod"}', True)]:
            with self.subTest(manifest=man):
                m = PS.load(self.folder(man, text))
                for s in m.states:
                    if ok:
                        self.assertIsNone(s.status, s.reason)
                    else:
                        self.assertEqual(s.status, "refused")
                        self.assertIn('mod m: player states have to be on the server and on every player\'s game: set "side": "both" and "clients": "mod"',
                                      s.reason)
        m = PS.parse(text, "m", "server", None)
        self.assertTrue(all(s.status == "refused" for s in m.states))
        with self.assertRaises(TypeError):
            PS.load(self.folder('{"id": "m"}', text), mod="m")
        m = PS.load(self.folder('{"id": "x"}', text), mod="m", side="both", clients="mod")
        self.assertEqual(m.mod, "m")
        self.assertTrue(all(s.status is None for s in m.states))

    def test_no_manifest(self):
        d = Path(self.tmp.name) / "loose"
        d.mkdir()
        (d / PS.FILE_NAME).write_text(json.dumps(self.NOTE))
        with mock.patch.object(PS, "find_manifest", return_value=None):
            with self.assertRaises(PS.DefinitionError) as e:
                PS.load(d / PS.FILE_NAME)
        self.assertIn("kiln.mod.json", str(e.exception))

    def test_ids_alike_in_one_file(self):
        with mock.patch.object(PS, "fnv_guid", return_value=0x1234):
            m = mod({"a": {"clip": "x", "like": "L"}, "b": {"clip": "x", "like": "L"}})
        for s, other in zip(m.states, "ba"):
            self.assertIn(f"is the id of state {other} too", s.reason)


class Rules(Base):
    def test_untouched_graph_round_trips(self):
        self.assertEqual(Encoder(TYPES).encode(TYPES.get(GRAPH), decode(self.raw)), self.raw)

    def test_rule1_clip_node(self):
        h0 = self.synth.hammer[0]
        v, _ = self.merged({"x": {"clip": "x", "like": h0}, "y": {"clip": "y", "like": h0, "loop": False, "speed": 1.25}})
        lc = self.game(f"clip.{h0}")
        c = self.node(v, "m", "x", "clip")
        g = mclip("x")
        self.assertEqual((c["animation"], c["animationHash"]["value"]), (g, A.fnv_guid(g)))
        self.assertEqual(c["animationDuration"], PS.f32(47 / 30))
        self.assertEqual((c["rootMotionAnimation"], c["rootMotionAnimationHash"]["value"]), (ZERO, 0), "the clip has no root motion")
        self.assertIs(c["hasSequences"], False)
        for f in PG.GENDER_GUIDS:
            self.assertEqual(c[f], ZERO)
        for f in PG.GENDER_HASHES:
            self.assertEqual(c[f]["value"], 0)
        self.assertNotEqual(lc["animationGenderAlternative"], ZERO, "the neighbour has one")
        self.assertIs(c["loop"], True, "the clip's own flag")
        self.assertEqual((c["additiveReferenceAnimation"], c["additiveReferenceAnimationHash"]["value"]), (g, A.fnv_guid(g)))
        self.assertEqual(c["speedController"], lc["speedController"])
        written = {"id", "animation", "animationHash", "animationDuration", "rootMotionAnimation", "rootMotionAnimationHash", "hasSequences", "loop",
                   "additiveReferenceAnimation", "additiveReferenceAnimationHash"} | set(PG.GENDER_GUIDS) | set(PG.GENDER_HASHES)
        for f in lc:
            if f not in written:
                self.assertEqual(c[f], lc[f], f)
        self.assertIs(c["useRootMotionRotation"], True)
        y = self.node(v, "m", "y", "clip")
        self.assertIs(y["loop"], False)
        self.assertEqual(y["speedController"]["rawValueController"]["speedMultiplier"], 1.25)
        # root motion and sequences from the facts
        self.facts[mclip("z")] = facts(rm=True, seq=True, fps=60, frames=61)
        v, _ = self.merged({"z": {"clip": "z", "like": h0}})
        c = self.node(v, "m", "z", "clip")
        self.assertEqual((c["rootMotionAnimation"], c["rootMotionAnimationHash"]["value"]), (mclip("z"), A.fnv_guid(mclip("z"))))
        self.assertIs(c["hasSequences"], True)
        self.assertEqual(c["animationDuration"], PS.f32(61 / 60))

    def test_rule1_first_clip_depth_first_and_additive(self):
        v, _ = self.merged({"x": {"clip": "x", "like": "Attack_Unarmed_01"}, "y": {"clip": "y", "like": "Attack_Hammer_Additive"}})
        a, b, c = self.game("clip.u1a"), self.game("clip.u1b"), self.node(v, "m", "x", "clip")
        self.assertNotEqual((a["preventRootMotion"], a["loopMode"]), (b["preventRootMotion"], b["loopMode"]))
        self.assertEqual((c["preventRootMotion"], c["loopMode"]), (a["preventRootMotion"], a["loopMode"]), "the first input of the select, inputA")
        self.assertEqual(c["animationDuration"], PS.f32(47 / 30))
        y, add = self.node(v, "m", "y", "clip"), self.game("clip.ha.add")
        self.assertEqual(y["additiveType"], "Additive_LocalSpace")
        self.assertEqual((y["additiveReferenceAnimation"], y["additiveReferenceAnimationHash"]), (cg("ref"), add["additiveReferenceAnimationHash"]),
                         "an additive node keeps the neighbour's reference")
        _, rep = self.merge(mod({"b": {"clip": "x", "like": "Attack_Hammer_Blend"}, "ok": {"clip": "x", "like": "Parry_FullBody"}}))
        self.refused(rep, "b", "no SampleAnimationClip")
        self.assertEqual(self.status(rep, "ok")["status"], "live")

    def test_rule2_layer_blend_over_a_cached_pose(self):
        v, _ = self.merged({"guard": {"clip": "guard", "like": "Block_FullBody"}})
        lb, game_lb = self.node(v, "m", "guard", "layer"), self.game("lb.block")
        self.assertEqual(lb["base"], game_lb["base"])
        self.assertEqual(self.by_id(v)[lb["base"]["value"]]["type"], "UseCachedPose")
        self.assertEqual(lb["blendOptions"], game_lb["blendOptions"])
        self.assertEqual(lb["blendOptions"]["boneMask"], self.synth.mask)
        self.assertEqual(lb["layer"]["value"], PG.node_id("m", "guard", "clip"))
        self.assertEqual(self.node(v, "m", "guard", "pose")["input"]["value"], PG.node_id("m", "guard", "layer"))
        self.assertEqual(self.node(v, "m", "guard", "clip")["animation"], mclip("guard"), "the clip under the guard's select is replaced by the mod's")
        v, _ = self.merged({"p": {"clip": "x", "like": "Parry_FullBody"}})
        self.assertEqual(self.node(v, "m", "p", "pose")["input"]["value"], PG.node_id("m", "p", "clip"))
        self.assertNotIn(PG.node_id("m", "p", "layer"), self.by_id(v))
        _, rep = self.merge(mod({"k": {"clip": "x", "like": "Attack_Skill_Kick_FullBody"}, "ok": {"clip": "x", "like": "Parry_FullBody"}}))
        self.refused(rep, "k", "LayerBlend over a SampleAnimationClip, not over a cached pose")
        self.assertEqual(self.status(rep, "ok")["status"], "live")

    def test_rule3_state_node(self):
        v, rep = self.merged({"a": {"clip": "x", "like": "Parry_FullBody", "blendIn": 0.07}, "b": {"clip": "x", "like": "Parry_FullBody"}})
        a, b, parry = self.node(v, "m", "a", "state"), self.node(v, "m", "b", "state"), self.game("s.parry")
        self.assertEqual(a["debugName"], "m.a")
        self.assertEqual(a["poseResult"]["value"], PG.node_id("m", "a", "pose"))
        self.assertEqual(a["retriggerBlendDuration"], PS.f32(0.07))
        self.assertEqual(b["retriggerBlendDuration"], parry["retriggerBlendDuration"])
        self.assertNotEqual(parry["retriggerBlendDuration"], PS.f32(0.07))
        for f in parry:
            if f not in ("id", "debugName", "poseResult", "retriggerBlendDuration"):
                self.assertEqual(a[f], parry[f], f)
        e = self.status(rep, "a")
        self.assertEqual((e["kind"], e["machine"], e["status"], e["guid"], e["id"]), ("state", "SM_Main", "live", G.derive("m", "state", "a"),
                                                                                     f"0x{A.fnv_guid(G.derive('m', 'state', 'a')):08x}"))
        self.assertEqual(set(e), {"mod", "name", "guid", "id", "kind", "like", "machine", "status", "reason"})

    def gt_to(self, m, state_id):
        return [e for e in m["globalTransitions"][:m["numGlobalTransitions"]] if e["toState"]["value"] == state_id]

    def test_rule4_entry(self):
        h0 = self.synth.hammer[0]
        v, _ = self.merged({"a": {"clip": "x", "like": h0, "blendIn": 0.05}, "b": {"clip": "x", "like": h0}, "j": {"clip": "x", "like": "Jump_Attack_End_FullBody"}})
        ha, game_ha = self.machine(v, "SM_Attack_Hammer_FullBody"), self.game("sm.ha")
        self.assertEqual(ha["numGlobalTransitions"], game_ha["numGlobalTransitions"] + 2)
        self.assertEqual(ha["globalTransitions"][:game_ha["numGlobalTransitions"]], game_ha["globalTransitions"][:game_ha["numGlobalTransitions"]])
        (ga,), (gb,) = self.gt_to(ha, PG.node_id("m", "a", "state")), self.gt_to(ha, PG.node_id("m", "b", "state"))
        self.assertEqual(ga["duration"], PS.f32(0.05))
        self.assertEqual(gb["duration"], PS.f32(0.3))
        self.assertEqual(ga["rootMotionMode"], "UseRootMotionFromHighestWeight", "modes are the neighbour's")
        for gt, name in ((ga, "a"), (gb, "b")):
            c = self.by_id(v)[gt["conditionNode"]["value"]]
            self.assertEqual(c, {"id": {"value": PG.node_id("m", name, "cond")}, "type": "IdParameterEqualsConstant", "parameterHash": {"value": SI},
                                 "parameterId": {"value": SI}, "value": {"value": A.fnv_guid(G.derive("m", "state", name))},
                                 "ignoreForAnimationSequences": False})
        self.assertEqual([x["value"] for x in ha["states"][-2:]], [PG.node_id("m", "a", "state"), PG.node_id("m", "b", "state")])
        (gj,) = self.gt_to(self.machine(v, "SM_Jump_Attack_FullBody"), PG.node_id("m", "j", "state"))
        self.assertEqual(self.by_id(v)[gj["conditionNode"]["value"]]["type"], "IdParameterEqualsConstant", "a single id even where the neighbour's is a list")
        # no global transition: the first local transition into the neighbour
        v, _ = self.merged({"u": {"clip": "x", "like": "Attack_Unarmed_01"}})
        (gu,) = self.gt_to(self.machine(v, "SM_Attack_Unarmed_FullBody"), PG.node_id("m", "u", "state"))
        t21 = self.game("t.u21")
        self.assertEqual((gu["duration"], gu["rootMotionMode"], gu["eventMode"], gu["blendCurve"], gu["blendProfile"]),
                         (t21["duration"], "UseRootMotionFromDestinationState", t21["eventMode"], t21["blendCurve"], t21["blendProfile"]))
        _, rep = self.merge(mod({"l": {"clip": "x", "like": "Attack_Unarmed_Lonely"}, "ok": {"clip": "x", "like": "Attack_Unarmed_02"}}))
        self.refused(rep, "l", "no global transition and no local transition into it")
        self.assertEqual(self.status(rep, "ok")["status"], "live")

    def test_rule5_local_transitions(self):
        v, _ = self.merged({"d": {"clip": "x", "like": "DodgeRoll_FullBody"}, "a": {"clip": "x", "like": "Dodge_Attack_FullBody"},
                            "s1": {"clip": "x", "like": "Attack_Sword_Shield_01"}, "s2": {"clip": "x", "like": "Attack_Sword_Shield_02"},
                            "u": {"clip": "x", "like": "Attack_Unarmed_02"}})
        by = self.by_id(v)
        main = self.machine(v, "SM_Main")
        new = [x["value"] for x in main["transitions"][len(self.game("sm.main")["transitions"]):]]
        out0, out1 = by[PG.node_id("m", "d", "out.0")], by[PG.node_id("m", "d", "out.1")]
        game0, game1 = self.game("t.dr.da"), self.game("t.dr.j")
        for o, gm in ((out0, game0), (out1, game1)):
            self.assertEqual(o["fromState"]["value"], PG.node_id("m", "d", "state"))
            self.assertEqual((o["toState"], o["conditionNode"], o["duration"]), (gm["toState"], gm["conditionNode"], gm["duration"]), "the same condition node")
        into = by[PG.node_id("m", "a", "in.0")]
        self.assertEqual((into["fromState"], into["toState"]["value"]), (game0["fromState"], PG.node_id("m", "a", "state")))
        c = by[into["conditionNode"]["value"]]
        self.assertEqual((c["id"]["value"], c["type"], c["value"]["value"]), (PG.node_id("m", "a", "in.0.cond"), "IdParameterEqualsConstant",
                                                                           A.fnv_guid(G.derive("m", "state", "a"))))
        self.assertEqual(new, [PG.node_id("m", "a", "in.0"), PG.node_id("m", "d", "out.0"), PG.node_id("m", "d", "out.1")], "added in state order")
        self.assertNotIn(PG.node_id("m", "a", "out.0"), by)
        self.assertNotIn(PG.node_id("m", "a", "in.1"), by, "a way in on another id is not copied")
        s1o, s2i = by[PG.node_id("m", "s1", "out.0")], by[PG.node_id("m", "s2", "in.0")]
        self.assertEqual(s1o["toState"], self.game("t.s12")["toState"])
        self.assertEqual(s2i["fromState"], self.game("t.s12")["fromState"])
        self.assertEqual(s2i["duration"], self.game("t.s12")["duration"])
        sw = self.machine(v, "SM_Attack_Sword_Shield_FullBody")
        self.assertEqual(len(sw["transitions"]), 3)
        u = [x["value"] for x in self.machine(v, "SM_Attack_Unarmed_FullBody")["transitions"]]
        self.assertEqual(u[2:], [PG.node_id("m", "u", "out.0"), PG.node_id("m", "u", "in.0")])

    def values(self, v, nid):
        return [x["value"] for x in self.by_id(v)[nid]["values"]]

    def test_rule6_lists(self):
        h0 = self.synth.hammer[0]
        v, rep = self.merged({"h": {"clip": "x", "like": h0}, "j": {"clip": "x", "like": "Jump_Attack_Start_FullBody"},
                              "a": {"clip": "x", "like": "Dodge_Attack_FullBody"}})
        s = self.synth
        hid, jid, aid = (A.fnv_guid(G.derive("m", "state", x)) for x in "hja")
        before = decode(self.raw)
        for lst, want in [(s.n.nid("l.w.ha"), [hid]), (s.main_melee_list, [hid]), (s.reset, [hid, jid, aid]), (s.main_jump_list, [jid]),
                          (s.n.nid("l.dr.j"), [jid])]:
            self.assertEqual(self.values(v, lst), self.values(before, lst) + want)
        for own in (s.n.nid("l.Jump_Attack_Start_FullBody"), s.n.nid("l.da"), s.n.nid("l.dr.da"), s.partial, s.n.nid("l.w.sw"), s.n.nid("l.Jump_Attack_End_FullBody")):
            self.assertEqual(self.values(v, own), self.values(before, own), "own entry, or not every entry id")
        used = [x["value"] for x in v["usedInputIds"]["parameterIds"]]
        self.assertEqual(used, [x["value"] for x in before["usedInputIds"]["parameterIds"]] + [hid, jid, aid], "machines in graph order")
        self.assertEqual((rep["lists"], rep["listEntries"], rep["usedInputIds"]), (5, 7, 3))

    def test_rule7_selects(self):
        v, rep = self.merged({"h": {"clip": "x", "like": self.synth.hammer[0]}, "s": {"clip": "x", "like": "Sneak_Attack_FullBody"}})
        before = decode(self.raw)
        sel, sb = self.by_id(v)[self.synth.n.nid("sel.ov")], self.by_id(before)[self.synth.n.nid("sel.ov")]
        self.assertEqual(sel["values"], sb["values"] + [{"value": A.fnv_guid(G.derive("m", "state", "h"))}])
        self.assertEqual(sel["inputPoses"], sb["inputPoses"] + [{"value": self.synth.ov_pose}])
        self.assertEqual(self.by_id(v)[self.synth.n.nid("sel.sneak")], self.by_id(before)[self.synth.n.nid("sel.sneak")], "inside the neighbour's own pose")
        self.assertEqual(rep["selects"], 1)

    def fill(self, n, hammer=4, prefix="s"):
        return {f"{prefix}{i:02d}": {"clip": "x", "like": f"Attack_Hammer_{i % hammer:02d}"} for i in range(n)}

    def synth_raw(self, hammer):
        s = Synth(hammer)
        self.facts.update(s.facts)
        return s, s.raw()

    def test_rule8_fill_then_nest_then_full(self):
        s, raw = self.synth_raw(4)
        num = s.value_of(s.n.nid("sm.ha"))["numGlobalTransitions"]
        self.assertEqual(num, 6)
        out, rep = self.merge(mod(self.fill(34 - num)), raw=raw)
        v = decode(out)
        ha = self.machine(v, "SM_Attack_Hammer_FullBody")
        self.assertEqual((ha["numGlobalTransitions"], rep["machines"][0]["nested"]), (34, 0))
        # one more: all of them nested, behind one wrapper
        states = dict(self.fill(34 - num + 1), first={"clip": "x", "like": "Attack_Hammer_00", "blendIn": 0.05})
        out, rep = self.merge(mod(states), raw=raw)
        self.assertEqual(rep["graph"], "merged", rep)
        v = decode(out)
        by = self.by_id(v)
        ha = self.machine(v, "SM_Attack_Hammer_FullBody")
        self.assertEqual(ha["numGlobalTransitions"], num + 1)
        names = sorted(states, key=str.encode)
        wrapper_id = PG.node_id("nest", "SM_Attack_Hammer_FullBody", 0, "wrapper")
        self.assertEqual(ha["states"][-1]["value"], wrapper_id)
        (wgt,) = self.gt_to(ha, wrapper_id)
        lst = by[wgt["conditionNode"]["value"]]
        self.assertEqual(lst["id"]["value"], PG.node_id("nest", "SM_Attack_Hammer_FullBody", 0, "list"))
        self.assertEqual([x["value"] for x in lst["values"]], [A.fnv_guid(G.derive("m", "state", x)) for x in names])
        self.assertEqual(wgt["duration"], PS.f32(0.3), "the neighbour's duration, not the first state's blendIn")
        self.assertEqual(wgt["rootMotionMode"], "UseRootMotionFromHighestWeight")
        inner = by[PG.node_id("nest", "SM_Attack_Hammer_FullBody", 0, "machine")]
        self.assertEqual(inner["debugName"], "kiln.nest.SM_Attack_Hammer_FullBody.0")
        self.assertEqual(inner["parentStateMachineStateId"]["value"], wrapper_id)
        self.assertEqual(inner["entryState"]["value"], PG.node_id("m", names[0], "state"))
        self.assertEqual([x["value"] for x in inner["states"]], [PG.node_id("m", x, "state") for x in names])
        self.assertEqual(inner["numGlobalTransitions"], len(names))
        self.assertEqual(inner["transitions"], [])
        self.assertEqual(by[wrapper_id]["parentStateMachineId"]["value"], ha["id"]["value"])
        self.assertEqual(by[by[wrapper_id]["poseResult"]["value"]]["input"]["value"], inner["id"]["value"])
        for x in names:
            st = by[PG.node_id("m", x, "state")]
            self.assertEqual(st["parentStateMachineId"]["value"], inner["id"]["value"])
            (g,) = self.gt_to(inner, st["id"]["value"])
            self.assertEqual(by[g["conditionNode"]["value"]]["value"]["value"], A.fnv_guid(G.derive("m", "state", x)))
        (gf,) = self.gt_to(inner, PG.node_id("m", "first", "state"))
        self.assertEqual(gf["duration"], PS.f32(0.05), "blendIn inside the nested machine")
        self.assertIn(A.fnv_guid(G.derive("m", "state", "first")), self.values(v, s.n.nid("l.w.ha")), "rule 6 still applies")
        # full: one free global transition, so one group of 34; the 35th state in mod and name order is refused
        s, raw = self.synth_raw(31)
        self.assertEqual(s.value_of(s.n.nid("sm.ha"))["numGlobalTransitions"], 33)
        states = self.fill(35, hammer=31)
        out, rep = self.merge(mod(states), raw=raw)
        self.assertEqual(rep["graph"], "merged", rep)
        last = sorted(states, key=str.encode)[-1]
        self.refused(rep, last, "is full")
        self.assertEqual(sum(1 for e in rep["states"] if e["status"] == "live"), 34)
        self.assertEqual(self.machine(decode(out), "SM_Attack_Hammer_FullBody")["numGlobalTransitions"], 34)

    def test_rule8_nested_states_have_no_local_transitions(self):
        s, raw = self.synth_raw(4)
        states = {f"u{i:02d}": {"clip": "x", "like": "Attack_Unarmed_0" + "12"[i % 2]} for i in range(35)}
        out, rep = self.merge(mod(states), raw=raw)
        self.assertEqual(rep["graph"], "merged", rep)
        v = decode(out)
        un = self.machine(v, "SM_Attack_Unarmed_FullBody")
        self.assertEqual(un["numGlobalTransitions"], 2, "two nested machines (34 + 1)")
        self.assertEqual(len(un["transitions"]), 2, "the game's own only")
        self.assertFalse(any(PG.node_id("m", x, "out.0") in self.by_id(v) for x in states))

    def test_rule9_placement_and_backward_rule(self):
        v, _ = self.merged({"a": {"clip": "x", "like": "Dodge_Attack_FullBody"}, "d": {"clip": "x", "like": "DodgeRoll_FullBody"},
                            "g": {"clip": "guard", "like": "Block_FullBody"}, "h": {"clip": "x", "like": self.synth.hammer[0]}})
        ids = [n["value"]["id"]["value"] for n in v["nodeDefinitions"]]
        main = ids.index(self.synth.n.nid("sm.main"))
        r = lambda s, role: PG.node_id("m", s, role)  # noqa: E731
        block = [r("a", "clip"), r("a", "pose"), r("a", "state"), r("a", "cond"), r("a", "in.0.cond"), r("a", "in.0"),
                 r("d", "clip"), r("d", "pose"), r("d", "state"), r("d", "cond"), r("d", "out.0"), r("d", "out.1"),
                 r("g", "clip"), r("g", "layer"), r("g", "pose"), r("g", "state"), r("g", "cond")]
        self.assertEqual(ids[main - len(block):main], block)
        ha = ids.index(self.synth.n.nid("sm.ha"))
        self.assertEqual(ids[ha - 4:ha], [r("h", "clip"), r("h", "pose"), r("h", "state"), r("h", "cond")])
        game = [n["value"]["id"]["value"] for n in decode(self.raw)["nodeDefinitions"]]
        self.assertEqual([i for i in ids if i in set(game)], game)
        pos = {x: i for i, x in enumerate(ids)}
        for i, n in enumerate(v["nodeDefinitions"]):
            for path in PG.REFS.get(n["value"]["type"], ()):
                for x in PG._get_path(n["value"], path):
                    if x and (n["value"]["type"], path) not in PG.PARENT_REFS:
                        self.assertLess(pos[x], i, (n["value"]["type"], path))

    def test_rule9_nested_placement(self):
        s, raw = self.synth_raw(4)
        states = self.fill(30)
        out, _ = self.merge(mod(states), raw=raw)
        ids = [n["value"]["id"]["value"] for n in decode(out)["nodeDefinitions"]]
        ha = ids.index(s.n.nid("sm.ha"))
        nest = [PG.node_id("nest", "SM_Attack_Hammer_FullBody", 0, x) for x in ("machine", "pose", "wrapper", "list")]
        self.assertEqual(ids[ha - 4:ha], nest)
        last = sorted(states, key=str.encode)[-1]
        self.assertEqual(ids[ha - 8:ha - 4], [PG.node_id("m", last, x) for x in ("clip", "pose", "state", "cond")])
        first = sorted(states, key=str.encode)[0]
        self.assertEqual(ids[ha - 4 - 4 * 30], PG.node_id("m", first, "clip"))

    def test_rule10_node_ids(self):
        self.assertEqual(PG.node_id("m", "x", "clip"), fnv1a32(b"kiln.player.m.x.clip"))
        self.assertEqual(PG.node_id("nest", "SM_Main", 0, "list"), fnv1a32(b"kiln.player.nest.SM_Main.0.list"))
        # an id the graph has
        val = decode(self.raw)
        clash = dict(val["nodeDefinitions"][0], value=dict(val["nodeDefinitions"][0]["value"], id={"value": PG.node_id("m", "x", "pose")}))
        val["nodeDefinitions"].insert(0, clash)
        raw = Encoder(TYPES).encode(TYPES.get(GRAPH), val)
        out, rep = self.merge(mod({"x": {"clip": "x", "like": "Parry_FullBody"}, "y": {"clip": "x", "like": "Parry_FullBody"}}), raw=raw)
        self.refused(rep, "x", "node ids is taken")
        self.assertEqual(self.status(rep, "y")["status"], "live")
        # a state id the graph compares
        val = decode(self.raw)
        val["usedInputIds"]["parameterIds"].append({"value": A.fnv_guid(G.derive("m", "state", "taken"))})
        raw = Encoder(TYPES).encode(TYPES.get(GRAPH), val)
        _, rep = self.merge(mod({"taken": {"clip": "x", "like": "Parry_FullBody"}, "y": {"clip": "x", "like": "Parry_FullBody"}}), raw=raw)
        self.refused(rep, "taken", "already compares")
        # two of ours alike
        real = PG.node_id
        fake = lambda *p: 77 if p[1:] in (("p", "clip"), ("q", "cond")) else real(*p)  # noqa: E731
        with mock.patch.object(PG, "node_id", fake):
            _, rep = self.merge(mod({"p": {"clip": "x", "like": "Parry_FullBody"}, "q": {"clip": "x", "like": "Parry_FullBody"}}),
                                mod({"r": {"clip": "x", "like": "Parry_FullBody"}}, name="other"))
        self.refused(rep, "p", "node id is the same as m/q's")
        self.refused(rep, "q", "node id is the same as m/p's")
        self.assertEqual(self.status(rep, "r", "other")["status"], "live")

    def test_rule11_order_does_not_change_a_byte(self):
        a = {f"a{i}": {"clip": "x", "like": lk} for i, lk in enumerate(["Parry_FullBody", "Block_FullBody", "DodgeRoll_FullBody", self.synth.hammer[1],
                                                                        "Jump_Attack_Loop_FullBody", "Attack_Unarmed_02", "Dodge_Attack_FullBody"])}
        b = {f"b{i}": {"clip": "y", "like": lk} for i, lk in enumerate(["Parry_FullBody", self.synth.hammer[0], "Attack_Sword_Shield_02"])}
        ref, rep = self.merge(mod(a, "alpha"), mod(b, "beta"))
        self.assertEqual(rep["graph"], "merged")
        rng = random.Random(7)
        for _ in range(4):
            ka, kb = list(a), list(b)
            rng.shuffle(ka)
            rng.shuffle(kb)
            mods = [mod({k: a[k] for k in ka}, "alpha"), mod({k: b[k] for k in kb}, "beta")]
            rng.shuffle(mods)
            out, rep2 = self.merge(*mods)
            self.assertEqual(out, ref)
            self.assertEqual(rep2, rep)
        self.assertEqual([(e["mod"], e["name"]) for e in rep["states"]], sorted(((e["mod"], e["name"]) for e in rep["states"]), key=lambda x: (x[0].encode(), x[1].encode())))
        # in a machine: mod id first, then name, as bytes
        main = [x["value"] for x in self.machine(decode(ref), "SM_Main")["states"]][-5:]
        self.assertEqual(main, [PG.node_id("alpha", x, "state") for x in ("a0", "a1", "a2", "a6")] + [PG.node_id("beta", "b0", "state")])

    def test_rule12_nothing_to_do(self):
        called = []

        def raw():
            called.append(1)
            return self.raw
        for mods in ([], [mod({})], [mod({"a": {"clip": "x", "like": "Parry_FullBody"}}, side="server")]):
            out, rep = PG.merge(raw, TYPES, mods, self.facts.get)
            self.assertIsNone(out)
            self.assertEqual(rep["graph"], "untouched")
        self.assertEqual(called, [], "the game's graph is not read when no state is left")
        out, rep = self.merge(mod({"a": {"clip": "x", "like": "Nope"}}))
        self.assertEqual((out, rep["graph"], rep["bytes"]["before"], rep["bytes"]["after"]), (None, "untouched", len(self.raw), None))


class Refusals(Base):
    def test_like(self):
        st = {"unknown": {"clip": "x", "like": "No_Such_State"}, "ambiguous": {"clip": "x", "like": "Attack_Heavy"},
              "qualified": {"clip": "x", "like": "SM_Attack_Dual_Daggers_FullBody/Attack_Heavy"}, "idle-block": {"clip": "x", "like": "Idle_Block"},
              "move-block": {"clip": "x", "like": "SM_Block_Locomotion/Move_Block"}, "grounded": {"clip": "x", "like": "GroundedLocomotion_FullBody"},
              "upper": {"clip": "x", "like": "DoNothing"}, "wrong-machine": {"clip": "x", "like": "SM_Main/Attack_Heavy"}, "ok": {"clip": "x", "like": "Parry_FullBody"}}
        _, rep = self.merge(mod(st))
        self.refused(rep, "unknown", "no state of the player's graph has that name")
        self.refused(rep, "ambiguous", "SM_Attack_Dual_Daggers_FullBody/Attack_Heavy, SM_Attack_Sword_Shield_FullBody/Attack_Heavy")
        self.refused(rep, "idle-block", "chosen by movement")
        self.refused(rep, "idle-block", "\"like\": \"Block_FullBody\"")
        self.refused(rep, "move-block", "chosen by movement")
        self.refused(rep, "grounded", "cannot be a neighbour yet")
        self.refused(rep, "upper", "SM_UpperBodyOverride/DoNothing cannot be a neighbour")
        self.refused(rep, "wrong-machine", "no state")
        for name in ("qualified", "ok"):
            self.assertEqual(self.status(rep, name)["status"], "live")
        self.assertEqual(rep["graph"], "merged")

    def test_clips(self):
        self.facts[mclip("other")] = facts(hier=0xDEAD, joints=60)
        self.facts[mclip("nofps")] = facts(fps=0)
        st = {"unserved": {"clip": "nope", "like": "Parry_FullBody"}, "skeleton": {"clip": "other", "like": "Parry_FullBody"},
              "fps": {"clip": "nofps", "like": "Parry_FullBody"}, "ok": {"clip": "x", "like": "Parry_FullBody"}}
        _, rep = self.merge(mod(st))
        self.refused(rep, "unserved", "is not served")
        self.refused(rep, "skeleton", "another skeleton: hierarchyHash 0x000000000000dead, 60 joints; the player's is 0x1122334455667788, 102 joints")
        self.refused(rep, "fps", "0 frames a second")
        self.assertEqual(self.status(rep, "ok")["status"], "live")
        f = dict(self.facts)
        del f[cg("parry")]
        _, rep = self.merge(mod({"p": {"clip": "x", "like": "Parry_FullBody"}}), facts=f)
        self.refused(rep, "p", "the neighbour's clip")

    def test_a_mod_given_twice_and_a_refused_mod_stops_no_other(self):
        a, b = mod({"a": {"clip": "x", "like": "Parry_FullBody"}}), mod({"b": {"clip": "x", "like": "Parry_FullBody"}})
        b.source = "other/player-states.json"
        c = mod({"c": {"clip": "x", "like": "Parry_FullBody"}}, name="c-mod")
        _, rep = self.merge(a, b, c)
        self.refused(rep, "a", "is given more than once")
        self.refused(rep, "b", "is given more than once")
        self.assertEqual(self.status(rep, "c", "c-mod")["status"], "live")
        bad = mod({"s": {"clip": "x", "like": "Parry_FullBody"}}, name="srv", side="server")
        _, rep = self.merge(bad, c)
        self.refused(rep, "s", 'set "side": "both" and "clients": "mod"', mod_="srv")
        self.assertIsNone(c.states[0].status, "merge leaves the definitions as they were")
        self.assertEqual((self.status(rep, "c", "c-mod")["status"], rep["graph"]), ("live", "merged"))


class Off(Base):
    def assertOff(self, out, rep, word):
        self.assertIsNone(out)
        self.assertEqual(rep["graph"], "off")
        self.assertIn(word, rep["reason"])
        for e in rep["states"]:
            self.assertEqual(e["status"], "refused")
        self.assertEqual((rep["nodesAdded"], rep["machines"]), (0, []))

    def states(self):
        return mod({"a": {"clip": "x", "like": "Parry_FullBody"}, "b": {"clip": "x", "like": "Parry_FullBody", "speed": 0}})

    def test_a_field_the_game_renamed(self):
        for tname, field in ((PG.NODE + "StateMachineStateNodeDefinition", "retriggerBlendDuration"),
                             ("keen::anim_graph::AnimationSpeedControllerRawValue", "speedMultiplier"), (PG.GT_TYPE, "blendCurve")):
            with self.subTest(field=field):
                dump = json.loads(json.dumps(TYPES.dump))
                t = next(x for x in dump["types"] if x["name"] == tname)
                for f in t["fields"]:
                    if f["name"] == field:
                        f["name"] = field + "Renamed"
                out, rep = self.merge(self.states(), types=TypeDb(dump))
                self.assertOff(out, rep, f"no field {field}")
                self.assertIn("the merge is off", self.status(rep, "a")["reason"])
                self.assertIn("speed", self.status(rep, "b")["reason"], "a state refused for its own reason keeps it")

    def test_a_dangling_reference(self):
        val = decode(self.raw)
        p = next(n["value"] for n in val["nodeDefinitions"] if n["value"]["type"] == "PoseResult")
        p["input"]["value"] = 0x0BADF00D
        out, rep = self.merge(self.states(), raw=Encoder(TYPES).encode(TYPES.get(GRAPH), val))
        self.assertOff(out, rep, "is no node")

    def test_a_forward_reference(self):
        val = decode(self.raw)
        nodes = val["nodeDefinitions"]
        i = next(i for i, n in enumerate(nodes) if n["value"]["type"] == "PoseResult")
        nodes.insert(len(nodes) - 1, nodes.pop(i - 1))  # its input moves behind it
        out, rep = self.merge(self.states(), raw=Encoder(TYPES).encode(TYPES.get(GRAPH), val))
        self.assertOff(out, rep, "points forward")

    def test_bytes_that_do_not_encode_back(self):
        out, rep = self.merge(self.states(), raw=self.raw + b"\0\0\0\0")
        self.assertOff(out, rep, "does not encode back")
        out, rep = self.merge(self.states(), raw=self.raw[:100])
        self.assertOff(out, rep, "")

    def test_ids_twice_in_the_game(self):
        val = decode(self.raw)
        val["nodeDefinitions"][1]["value"]["id"] = dict(val["nodeDefinitions"][0]["value"]["id"])
        out, rep = self.merge(self.states(), raw=Encoder(TYPES).encode(TYPES.get(GRAPH), val))
        self.assertOff(out, rep, "the game's graph has node id")

    def test_the_merged_graph_must_decode_to_itself(self):
        class Lossy(Encoder):
            calls = 0

            def encode(self, t, v):
                Lossy.calls += 1
                out = super().encode(t, v)
                return out + b"\0" if Lossy.calls == 2 else out
        with mock.patch.object(PG, "Encoder", Lossy):
            out, rep = self.merge(self.states())
        self.assertOff(out, rep, "does not decode to itself")

    def overfill(self, v):
        """SM_Main with every entry in use and a count of 35: only the limit itself can refuse it."""
        m = self.machine(v, "SM_Main")
        gts = m["globalTransitions"]
        gts[m["numGlobalTransitions"]:] = [dict(gts[0]) for _ in range(A.MAX_GLOBAL - m["numGlobalTransitions"])]
        m["numGlobalTransitions"] = A.MAX_GLOBAL + 1

    def test_validate_refuses_a_changed_game_node(self):
        out, rep = self.merge(mod({"a": {"clip": "x", "like": "Parry_FullBody"}, "h": {"clip": "x", "like": self.synth.hammer[0]}}))
        merged, pristine = decode(out), decode(self.raw)
        new = {n["value"]["id"]["value"] for n in merged["nodeDefinitions"]} - {n["value"]["id"]["value"] for n in pristine["nodeDefinitions"]}
        machines = {self.synth.n.nid("sm.main"), self.synth.n.nid("sm.ha")}
        lists = {self.synth.n.nid(x) for x in ("l.w.ha", "l.main.melee", "l.reset")}
        selects = {self.synth.n.nid("sel.ov")}
        PG.validate(TYPES, pristine, merged, machines, lists, selects, new)
        cases = {
            "a game node": lambda v: v["nodeDefinitions"][0]["value"].update(loop=not v["nodeDefinitions"][0]["value"]["loop"]),
            "a machine's own transitions": lambda v: self.machine(v, "SM_Main")["globalTransitions"][0].update(duration=9.0),
            "a list lost an id": lambda v: self.by_id(v)[self.synth.reset]["values"].pop(0),
            "the header": lambda v: v.update(hasGenderMapping=not v["hasGenderMapping"]),
            "usedInputIds": lambda v: v["usedInputIds"]["parameterIds"].pop(0),
            "too many": self.overfill,
            "count": lambda v: self.machine(v, "SM_Main").update(numGlobalTransitions=self.machine(v, "SM_Main")["numGlobalTransitions"] - 1),
            "shape": lambda v: self.by_id(v)[PG.node_id("m", "a", "clip")]["speedController"]["rawValueController"].update(extra=1),
            "a missing field": lambda v: self.by_id(v)[PG.node_id("m", "a", "state")].pop("restState"),
            "a game node gone": lambda v: v["nodeDefinitions"].pop(0),
        }
        for what, change in cases.items():
            with self.subTest(what=what):
                bad = decode(out)
                change(bad)
                with self.assertRaises(PG.MergeOff):
                    PG.validate(TYPES, pristine, bad, machines, lists, selects, new)


class Facts(unittest.TestCase):
    """ClipFacts: a clip's facts from the mods' assets.json and files, else from the game."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def ja(self, fps=30, frames=45, joints=JOINTS, hier=HIER, flags=1):
        return Encoder(TYPES).encode(TYPES.get(PG.JOINT_ANIM), {"hierarchyHash": hier, "positionFactor": 1.0, "positionTangent": 1.0, "scaleFactor": 1.0,
                                                                "headers": [], "data": [], "fps": fps, "frameCount": frames, "jointCount": joints, "flags": flags})

    def assets(self, name, entries, files):
        d = self.dir / name
        d.mkdir()
        for f, b in files.items():
            (d / f).write_bytes(b)
        (d / "assets.json").write_text("// cooked by animcook\n" + json.dumps({"format": 1, "entries": entries}))
        return d

    def test_from_mods_and_the_game(self):
        g1, g2, g3, g4 = (str(G.derive("t", "clip", x)) for x in "abcd")
        a = self.assets("a", [{"kind": "resource", "guid": g1.upper(), "type": PG.JOINT_ANIM, "file": "a.bin"},
                              {"kind": "resource", "guid": g1, "type": PG.ROOT_MOTION, "part": 0, "file": "a.bin"},
                              {"kind": "resource", "guid": g2, "type": PG.JOINT_ANIM, "file": "b.bin", "side": "client"},
                              {"kind": "resource", "guid": g3, "type": f"0x{TYPES.get(PG.JOINT_ANIM)['hash']:08x}", "file": "c.bin"},
                              {"kind": "icon", "guid": g4, "file": "x.png"}],
                        {"a.bin": self.ja(), "b.bin": self.ja(), "c.bin": self.ja(fps=60, frames=10, flags=0)})
        b = self.assets("b", [{"kind": "resource", "guid": g1, "type": PG.SEQUENCES, "file": "s.bin"},
                              {"kind": "resource", "guid": g3, "type": PG.JOINT_ANIM, "file": "c.bin"}], {"s.bin": b"", "c.bin": self.ja(fps=60, frames=11)})

        class Game:
            def has(self, key):
                return key.guid in (g2, g4) and key[1] in (TYPES.get(PG.JOINT_ANIM)["hash"], TYPES.get(PG.ROOT_MOTION)["hash"])

            def read(self, key):
                return Facts.ja(None, fps=20, frames=5, flags=0)
        f = PG.ClipFacts(TYPES, [a, b], Game())
        self.assertEqual(f(g1), facts(fps=30, frames=45, loop=True, rm=True, seq=True))
        self.assertEqual(f(g2), facts(fps=20, frames=5, loop=False, rm=True, seq=False), "a client-only entry is not served on a server: the game's")
        self.assertIsNone(f(g3), "two mods serve the clip with other facts")
        self.assertEqual(f(g4)["fps"], 20)
        self.assertIsNone(PG.ClipFacts(TYPES, [a, b])(g4))
        self.assertIsNone(f(str(G.derive("t", "clip", "none"))))


class Cli(Base):
    """animcook.py player-graph and player-states with a graph file and no game: the clips' facts come from the mods' assets.json."""

    def setUp(self):
        super().setUp()
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.graph = self.dir / "graph.bin"
        self.graph.write_bytes(self.raw)
        self.game = self.make_mod("gamelike", {cg(x): self.synth.facts[cg(x)] for x in ("parry", "dr")}, None)
        self.mod = self.make_mod("m", {mclip("x"): facts(fps=30, frames=47, rm=False)},
                                 {"x": {"clip": "x", "like": "Parry_FullBody"}, "y": {"clip": "x", "like": "DodgeRoll_FullBody"}})

    def tearDown(self):
        self.tmp.cleanup()

    def make_mod(self, name, clips, states, side="both", clients="mod"):
        d = self.dir / name
        (d / "assets" / "cooked").mkdir(parents=True)
        (d / "kiln.mod.json").write_text(json.dumps({"id": name, "side": side, "clients": clients}))
        enc, ja = Encoder(TYPES), TYPES.get(PG.JOINT_ANIM)
        entries = []
        for i, (guid, f) in enumerate(clips.items()):
            raw = enc.encode(ja, {"hierarchyHash": f["hierarchyHash"], "positionFactor": 1.0, "positionTangent": 1.0, "scaleFactor": 1.0, "headers": [], "data": [],
                                  "fps": f["fps"], "frameCount": f["frameCount"], "jointCount": f["jointCount"], "flags": int(f["loop"])})
            (d / "assets" / "cooked" / f"c{i}.bin").write_bytes(raw)
            entries.append({"kind": "resource", "guid": guid, "type": PG.JOINT_ANIM, "part": 0, "file": f"cooked/c{i}.bin"})
            if f["rootMotion"]:
                entries.append({"kind": "resource", "guid": guid, "type": PG.ROOT_MOTION, "part": 0, "file": f"cooked/c{i}.bin"})
        (d / "assets" / "assets.json").write_text(json.dumps({"format": 1, "entries": entries}))
        if states is not None:
            (d / "assets" / PS.FILE_NAME).write_text(json.dumps({"format": 1, "states": states}))
        return d

    def run_cli(self, *args):
        return subprocess.run([sys.executable, str(ROOT / "animcook.py"), "--no-game", *map(str, args)], capture_output=True, text=True)

    def test_player_graph(self):
        out = self.dir / "out"
        r = self.run_cli("player-graph", "--mod", self.mod, "--mod", self.game, "--graph", self.graph, "--out", out)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("player state m/x: live", r.stdout)
        rep = json.loads((out / "player-graph.report.json").read_text())
        self.assertEqual(rep["graph"], "merged")
        data = (out / "player-graph.bin").read_bytes()
        ref, _ = self.merge(PS.load(self.mod / "assets" / PS.FILE_NAME),
                            facts=PG.ClipFacts(TYPES, [self.mod / "assets", self.game / "assets"]))
        self.assertEqual(data, ref)
        c = self.by_id(decode(data))[PG.node_id("m", "x", "clip")]
        self.assertEqual((c["animationDuration"], c["loop"], c["rootMotionAnimation"]), (PS.f32(47 / 30), False, ZERO))
        r2 = self.run_cli("player-graph", "--mod", self.game, "--mod", self.mod, "--graph", self.graph, "--out", out)
        self.assertEqual((out / "player-graph.bin").read_bytes(), data, "--mod order")
        # without the game's clips the neighbours' skeleton cannot be checked
        r = self.run_cli("player-graph", "--mod", self.mod, "--graph", self.graph, "--out", out)
        self.assertEqual(r.returncode, 1, r.stdout)
        self.assertIn("REFUSED", r.stdout)
        self.assertFalse((out / "player-graph.bin").exists(), "a stale graph is removed")
        r = self.run_cli("player-graph", "--mod", self.mod, "--graph", self.graph, "--out", ROOT / "tests" / "out")
        self.assertEqual(r.returncode, 2)
        self.assertIn("git work tree", r.stderr)
        self.assertFalse((ROOT / "tests" / "out").exists())
        r = self.run_cli("player-graph", "--mod", self.mod, "--mod", self.game, "--graph", self.graph, "--report-only")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("graph merged", r.stdout)
        self.assertEqual(r2.returncode, 0)

    def test_player_states(self):
        f = self.mod / "assets" / PS.FILE_NAME
        r = self.run_cli("player-states", f)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("not checked: like", r.stdout)
        r = self.run_cli("player-states", f, "--graph", self.graph)
        self.assertEqual(r.returncode, 1, "the neighbours' clips are not served by this mod")
        st = json.loads(f.read_text())
        st["states"]["bad"] = {"clip": "x", "like": "Parry_FullBody", "speed": 0}
        f.write_text(json.dumps(st))
        r = self.run_cli("player-states", f)
        self.assertEqual(r.returncode, 1)
        self.assertIn("player state m/bad: REFUSED: mod m, state bad: speed", r.stdout)

    def test_recipe_with_and_without_player_states(self):
        rec = self.mod / "assets" / "cook.json"
        rec.write_text(json.dumps({"format": 1, "tool": "modelcook", "steps": []}))
        r = self.run_cli("recipe", rec, "--no-dispatch")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("player state m/x: shape ok", r.stdout)
        side = json.loads((self.mod / "assets" / "cooked" / PS.FILE_NAME).read_text())
        self.assertEqual(side["mod"], "m")
        self.assertEqual(set(side["states"]["x"]), {"guid", "id", "kind", "machine", "like", "status", "reason"})
        st = json.loads((self.mod / "assets" / PS.FILE_NAME).read_text())
        st["states"]["bad"] = {"clip": "x"}
        (self.mod / "assets" / PS.FILE_NAME).write_text(json.dumps(st))
        r = self.run_cli("recipe", rec, "--no-dispatch")
        self.assertEqual(r.returncode, 1, "a refused state fails the cook")
        (self.mod / "assets" / PS.FILE_NAME).unlink()
        (self.mod / "assets" / "cooked" / PS.FILE_NAME).unlink()
        r = self.run_cli("recipe", rec, "--no-dispatch")
        self.assertEqual((r.returncode, r.stdout, r.stderr), (0, "", ""))
        self.assertFalse((self.mod / "assets" / "cooked" / PS.FILE_NAME).exists())


if __name__ == "__main__":
    unittest.main()
