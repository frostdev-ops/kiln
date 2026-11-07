# SPDX-License-Identifier: MIT
"""animcook's steps of a mod's assets/cook.json. Usage: docs/kiln/modding/animations.md; schema: schema/cook.schema.json.

    { "format": 1, "tool": "animcook", "out": "cooked", "mod": "my-mod",      // mod: default the id in the nearest kiln.mod.json
      "steps": [
        { "op": "skeleton", "name": "golem", "gltf": "src/golem.glb" },
        { "op": "clips", "skeleton": "golem", "gltf": "src/golem.glb",
          "clips": { "idle": { "animation": "Idle", "loop": true }, "slam": { "animation": "Slam", "rootMotion": true,
                     "events": [ { "at": 0.5, "noise": 8 }, { "at": 0.1, "foot": "left" } ] } } },
        { "op": "graph", "name": "golem", "skeleton": "golem", "slots": { "HandR": "hand_r" },
          "states": { "idle": { "engine": "Idle", "clip": "idle" }, "slam": { "clip": "slam", "blendIn": 0.1 } } }
      ] }

Every GUID follows from <mod>:<kind>:<name> (the same rule as Kiln.Blocks), so a state's id is the same here and in C#. An input of
another tool's shape ("inputs": game resources by GUID) can name a game skeleton ("skeleton": "@id") or be overridden ("guid": "@id").

The cook needs no installed game unless a step names an input: new resources are the author's own. The outputs go to `out`; next to
each resource a JSON sidecar says what it holds (`<name>.skeleton.json`, `<name>.graph.json`, `clips.json`).
"""
import hashlib
import json
from pathlib import Path

from kfc.archive import ResourceKey
from kfc.blob import Decoder
from kfc.blobwrite import Encoder

from . import blocks as B, clips as C, graphgen as G, retarget as RT, skeleton as K
from .gltfdoc import Doc

TOOL = "animcook"
OPS = ("skeleton", "clips", "graph")
KNOWN_TOOLS = ("modelcook", "soundcook", "animcook")
STEP_KEYS = {
    "skeleton": {"tool", "op", "comment", "name", "gltf", "skin", "root", "guid", "mode"},
    "clips": {"tool", "op", "comment", "skeleton", "gltf", "clips", "tolerance", "retarget"},
    "graph": {"tool", "op", "comment", "name", "skeleton", "guid", "mode", "slots", "entry", "states", "layers", "raw"},
}
CLIP_KEYS = {"animation", "loop", "rootMotion", "events", "guid", "mode", "tolerance", "comment"}
HIER = "keen::ModelHierarchyResource"
JOINT_ANIM = "keen::JointAnimation"
GRAPH = "keen::anim_graph::runtime_graph::AnimationGraphResource2_0"
USED_TYPES = (HIER, JOINT_ANIM, GRAPH, "keen::RootMotionAnimation", "keen::LveAnimation", "keen::AnimationSequenceContainer", "keen::SequenceResource",
              "keen::anim_graph::BoneMaskResource")


class RecipeError(Exception):
    pass


def sha256(b):
    return hashlib.sha256(b).hexdigest()


def tool_of(doc, st):
    return st.get("tool", doc.get("tool", "modelcook"))


def own_steps(doc):
    return [(i, st) for i, st in enumerate(doc.get("steps", [])) if tool_of(doc, st) == TOOL]


def other_tools(doc):
    return sorted({tool_of(doc, st) for st in doc.get("steps", [])} - {TOOL})


def report_name(doc):
    return "cook-report.json" if doc.get("tool", "modelcook") == TOOL else f"cook-report.{TOOL}.json"


def load(path):
    p = Path(path)
    try:
        doc = json.loads(p.read_text())
    except (OSError, json.JSONDecodeError) as e:
        raise RecipeError(f"{p}: {e}") from e
    if doc.get("format") != 1:
        raise RecipeError(f"{p}: format {doc.get('format')!r}, expected 1")
    try:
        doc = B.add_to(doc, p)  # the animations and bodies of the mod's blocks/*.json
    except B.BlocksError as e:
        raise RecipeError(str(e)) from e
    for i, st in enumerate(doc.get("steps", [])):
        tl = tool_of(doc, st)
        if tl not in KNOWN_TOOLS and tl != "impact":
            raise RecipeError(f"step {i}: tool {tl!r} (known: {', '.join(KNOWN_TOOLS)})")
        if tl != TOOL:
            continue
        op = st.get("op")
        if op not in OPS:
            raise RecipeError(f"step {i}: op {op!r} (known: {', '.join(OPS)})")
        unknown = set(st) - STEP_KEYS[op]
        if unknown:
            raise RecipeError(f"step {i} ({op}): unknown keys {sorted(unknown)}")
        for k in {"skeleton": ("name", "gltf"), "clips": ("skeleton", "gltf", "clips"), "graph": ("name", "skeleton")}[op]:
            if k not in st:
                raise RecipeError(f"step {i} ({op}): {k} missing")
        if op == "clips":
            for nm, c in st["clips"].items():
                bad = set(c) - CLIP_KEYS
                if bad:
                    raise RecipeError(f"step {i}, clip {nm}: unknown keys {sorted(bad)}")
        if op == "graph" and not st.get("states") and "raw" not in st:
            raise RecipeError(f"step {i} (graph): states missing")
    return doc


def sources_of(doc, base):
    out = list(doc.get("blocksSources", []))
    for _, st in own_steps(doc):
        for k in ("gltf", "raw"):
            if k in st:
                out.append(str(st[k]))
    return sorted(set(out))


def needs_cook(path):
    p = Path(path)
    doc = load(p)
    if not own_steps(doc):
        return False
    rep = p.parent / doc.get("out", "cooked") / report_name(doc)
    if not rep.exists():
        return True
    r = json.loads(rep.read_text())
    if r.get("recipe_sha256") != sha256(p.read_bytes()) or not r.get("ok"):
        return True
    for src, h in r.get("sources", {}).items():
        f = p.parent / src
        if not f.exists() or sha256(f.read_bytes()) != h:
            return True
    return any(not (p.parent / o["file"]).exists() or sha256((p.parent / o["file"]).read_bytes()) != o["sha256"] for o in r.get("outputs", []))


def mod_id(doc, recipe_path):
    if doc.get("mod"):
        return doc["mod"]
    for d in Path(recipe_path).resolve().parents:
        m = d / "kiln.mod.json"
        if m.exists():
            import re
            text = re.sub(r"^\s*//.*$", "", m.read_text(), flags=re.M)
            return json.loads(re.sub(r",(\s*[}\]])", r"\1", text))["id"]
    raise RecipeError(f"{recipe_path}: no \"mod\" and no kiln.mod.json above it: ids need the mod's id")


class Run:
    def __init__(self, path, types, kfc=None):
        self.p = Path(path)
        self.doc = load(self.p)
        self.dir = self.p.parent
        self.out = self.doc.get("out", "cooked")
        self.t, self.k = types, kfc
        self.dec, self.enc = Decoder(types, max_array=1 << 26), Encoder(types)
        self.mod = mod_id(self.doc, self.p)
        self.files, self.entries, self.reports = {}, [], []
        self.skeletons, self.clips, self.inputs = {}, {}, {}
        self.side = {"format": 1, "tool": TOOL, "mod": self.mod, "clips": {}}
        for iid, spec in self.doc.get("inputs", {}).items():
            self.inputs[iid] = spec

    def game(self, why):
        if self.k is None:
            raise RecipeError(f"{why} reads the installed game, and none was found (--game)")
        return self.k

    def input_guid(self, ref, want_type):
        iid = ref[1:]
        if iid not in self.inputs:
            raise RecipeError(f"{ref}: no such input")
        spec = self.inputs[iid]
        if spec.get("type") != want_type:
            raise RecipeError(f"{ref} is a {spec.get('type')}, needs {want_type}")
        return spec["guid"]

    def emit(self, stem, type_name, guid, raw, mode="new"):
        rel = f"{self.out}/{stem}.bin"
        self.files[rel] = raw
        self.entries.append({"kind": "resource", "guid": guid, "type": type_name, "part": 0, "file": rel, "mode": mode})
        return rel

    def encode(self, type_name, value):
        t = self.t.get(type_name)
        raw = self.enc.encode(t, value)
        if self.enc.encode(t, self.dec.decode(t, raw)) != raw:
            raise RecipeError(f"{type_name} does not re-encode byte-identical")
        return raw

    def skeleton_step(self, st):
        name = st["name"]
        doc = Doc(self.dir / st["gltf"])
        sk = K.from_gltf(doc, skin=st.get("skin"), root=st.get("root"))
        guid = st.get("guid") or G.derive(self.mod, "skeleton", name)
        sk.guid = guid
        raw = self.encode(HIER, sk.value(guid))
        self.emit(f"{name}.skeleton", HIER, guid, raw, st.get("mode", "new"))
        self.files[f"{self.out}/{name}.skeleton.json"] = json.dumps(sk.to_json(guid), indent=1).encode() + b"\n"
        self.skeletons[name] = sk
        return {"op": "skeleton", "name": name, "guid": guid, "joints": len(sk), "hierarchyHash": f"0x{sk.hierarchy_hash:016x}", "bytes": len(raw), "ok": True}

    def skeleton_ref(self, ref):
        if isinstance(ref, str) and ref.startswith("@"):
            guid = self.input_guid(ref, HIER)
            k = self.game(f"skeleton {ref}")
            t = self.t.get(HIER)
            sk = K.from_game(self.dec.decode(t, k.read(ResourceKey(guid, t["hash"], 0))), guid)
            return sk
        if ref not in self.skeletons:
            raise RecipeError(f"skeleton {ref!r} is not made by an earlier step (has {', '.join(self.skeletons) or 'none'})")
        return self.skeletons[ref]

    def clips_step(self, st):
        sk = self.skeleton_ref(st["skeleton"])
        doc = Doc(self.dir / st["gltf"])
        rt = self.retarget_plan(st, doc, sk) if st.get("retarget") else None
        if rt is None and sk.source != "game":
            hint = " (animations of a rig of their own play on it with \"retarget\")"
            try:
                problems = K.from_gltf(doc, skin=None, root=sk.names[0] if sk.names[0] != K.SYNTHETIC_ROOT else None).compare(sk) if st["gltf"] else []
            except K.SkeletonError as e:
                raise RecipeError(f"{st['gltf']} does not fit skeleton {st['skeleton']}: {e}{hint}") from e
            if problems:
                raise RecipeError(f"{st['gltf']} does not fit skeleton {st['skeleton']}: " + "; ".join(problems[:6]) + hint)
        results = []
        ok = True
        for name, c in st["clips"].items():
            rm_cfg = c.get("rootMotion")
            if rm_cfg is True:
                rm_cfg = {}
            tol = dict(st.get("tolerance") or {}, **(c.get("tolerance") or {}))
            try:
                if rt is None:
                    raw, info, rm, lv = C.cook_clip(doc, c.get("animation", name), sk, loop=c.get("loop"), tolerance=tol, root_motion=rm_cfg)
                else:
                    src_frames, n = C.sample_animation(doc, c.get("animation", name), rt.src)
                    raw, info, rm, lv = C.finish_clip([rt.frame(f) for f in src_frames], n, sk, c.get("animation", name), loop=c.get("loop"),
                                                      tolerance=tol, root_motion=rm_cfg)
            except C.ClipError as e:
                raise RecipeError(f"clip {name}: {e}") from e
            ref = c.get("guid")
            if isinstance(ref, str) and ref.startswith("@"):
                guid = self.input_guid(ref, JOINT_ANIM)
                mode = c.get("mode", "override")
            else:
                guid = ref or G.derive(self.mod, "clip", name)
                mode = c.get("mode", "new")
            self.emit(f"clip_{name}", JOINT_ANIM, guid, raw, mode)
            if rm is not None:
                self.emit(f"clip_{name}.rootmotion", "keen::RootMotionAnimation", guid, self.encode("keen::RootMotionAnimation", rm), mode)
                self.emit(f"clip_{name}.lve", "keen::LveAnimation", guid, self.encode("keen::LveAnimation", lv), mode)
                info["rootMotionEnd"] = rm["positions"][-1]
            events = c.get("events") or []
            if events:
                sg = G.derive(self.mod, "notify", name)
                seq = C.event_sequence_value(self.t, self.dec, f"anim_{name}", events, info["duration"])
                self.emit(f"clip_{name}.events", "keen::SequenceResource", sg, self.encode("keen::SequenceResource", seq))
                self.emit(f"clip_{name}.notify", "keen::AnimationSequenceContainer", guid, self.encode("keen::AnimationSequenceContainer", {"sequences": [sg]}), mode)
                info["events"] = len(events)
            e = info["errors"]
            limit = {"rotationDeg": 2.0, "position": 0.01, "scale": 0.01}
            good = all(e[k] <= limit[k] for k in limit)
            ok = ok and good
            self.clips[name] = {"guid": guid, "duration": info["duration"], "loop": info["loop"], "rootMotion": rm is not None,
                                "rootRotation": bool(rm_cfg and rm_cfg.get("rotation")), "events": bool(events), "skeleton": sk.guid}
            self.side["clips"][name] = dict(self.clips[name], frames=info["frames"], bytes=info["bytes"])
            results.append(dict(info, name=name, guid=guid, mode=mode, ok=good))
        out = {"op": "clips", "gltf": st["gltf"], "skeleton": sk.guid, "clips": results, "ok": ok}
        if rt is not None:
            out["retarget"] = rt.report()
        return out

    def retarget_plan(self, st, doc, sk):
        """The step's "retarget": the clips' own rig (its armature, as a skeleton step reads one) paired with the target skeleton."""
        cfg = st["retarget"] if isinstance(st["retarget"], dict) else {}
        try:
            src = K.from_gltf(doc, skin=cfg.get("skin"), root=cfg.get("root"))
            if cfg.get("reference"):
                cfg = dict(cfg, referenceFrame=RT.reference_frame(doc, src, cfg["reference"], C.sample_animation))
            return RT.Plan(src, sk, cfg)
        except (K.SkeletonError, RT.RetargetError, C.ClipError) as e:
            raise RecipeError(f"{st['gltf']}: {e}") from e

    def graph_step(self, st):
        name = st["name"]
        sk = self.skeleton_ref(st["skeleton"])
        guid = st.get("guid") or G.derive(self.mod, "graph", name)
        if isinstance(guid, str) and guid.startswith("@"):
            guid = self.input_guid(guid, GRAPH)
        if "raw" in st:
            value = json.loads((self.dir / st["raw"]).read_text())
            info = {"states": {}, "raw": st["raw"], "warnings": [], "nodes": len(value.get("nodeDefinitions", []))}
            masks = []
        else:
            comp = G.Compiler(self.mod, name, st, sk, {n: c for n, c in self.clips.items() if c["skeleton"] == sk.guid or sk.source == "game"})
            value, info = comp.compile()
            G.check_fields(self.t, value)
            masks = comp.masks
        raw = self.encode(GRAPH, value)
        self.emit(f"graph_{name}", GRAPH, guid, raw, st.get("mode", "new"))
        for mname, mg, mv in masks:
            self.emit(f"mask_{mname}", "keen::anim_graph::BoneMaskResource", mg, self.encode("keen::anim_graph::BoneMaskResource", mv))
        side = dict(info, format=1, tool=TOOL, guid=guid, skeleton=sk.guid, mod=self.mod)
        self.files[f"{self.out}/{name}.graph.json"] = json.dumps(side, indent=1).encode() + b"\n"
        return {"op": "graph", "name": name, "guid": guid, "bytes": len(raw), "ok": True, **{k: info[k] for k in ("nodes", "warnings") if k in info}, "states": len(info.get("states", {})), "nested": info.get("nested", False)}

    def run(self, update_manifest=False, game_info=None, types_info=None):
        for i, st in own_steps(self.doc):
            fn = {"skeleton": self.skeleton_step, "clips": self.clips_step, "graph": self.graph_step}[st["op"]]
            try:
                r = fn(st)
            except (K.SkeletonError, G.GraphError, ValueError) as e:
                raise RecipeError(f"step {i} ({st['op']}): {e}") from e
            r["step"] = i
            self.reports.append(r)
        out_dir = self.dir / self.out
        out_dir.mkdir(parents=True, exist_ok=True)
        self.files[f"{self.out}/clips.json"] = json.dumps(self.side, indent=1).encode() + b"\n"
        for rel, data in self.files.items():
            (self.dir / rel).parent.mkdir(parents=True, exist_ok=True)
            (self.dir / rel).write_bytes(data)
        mp = self.dir / "assets.json"
        man = json.loads(mp.read_text()) if mp.exists() else {"format": 1, "entries": []}
        have = {(e.get("kind"), e.get("file")): e for e in man.get("entries", [])}
        missing = [e for e in self.entries if (e["kind"], e["file"]) not in have]
        mismatched = [e for e in self.entries if (e["kind"], e["file"]) in have and have[(e["kind"], e["file"])] != e]
        if update_manifest and (missing or mismatched):
            keys = {(e["kind"], e["file"]) for e in self.entries}
            man["entries"] = [x for x in man.get("entries", []) if (x.get("kind"), x.get("file")) not in keys] + self.entries
            mp.write_text(json.dumps(man, indent=2) + "\n")
            missing, mismatched = [], []
        ok = all(r.get("ok") for r in self.reports) and not missing and not mismatched
        rep = {"recipe": str(self.p), "recipe_sha256": sha256(self.p.read_bytes()), "ok": ok, "mod": self.mod, "game": game_info, "types": types_info,
               "sources": {s: sha256((self.dir / s).read_bytes()) for s in sources_of(self.doc, self.dir)},
               "outputs": [{"file": rel, "size": len(d), "sha256": sha256(d)} for rel, d in self.files.items()],
               "manifest": {"missing": missing, "mismatched": mismatched}, "steps": self.reports}
        (out_dir / report_name(self.doc)).write_text(json.dumps(rep, indent=2) + "\n")
        return rep
