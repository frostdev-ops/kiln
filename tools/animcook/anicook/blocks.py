# SPDX-License-Identifier: MIT
"""Cook steps for the animations and bodies a mod's blocks/*.json defines (docs/kiln/modding/npc-blocks.md).

A block that brings its own files says so with a path in the mod's folder:

    "animations": { "golem-moves": { "clips": "models/golem.glb", "idle": "Idle", "walk": "Walk", "dead": "Die",
                                      "hit": { "clip": "Flinch" }, "states": { "slam": { "clip": "Slam", "speed": 1.2 } } } },
    "bodies":     { "golem-body":  { "model": "models/golem.glb", "slots": { "HandR": "hand_r" } } }

Both tools read this module when they load a recipe, so a mod's assets/cook.json needs no step for them: the skeleton, clips and graph steps
(animcook) and the model step (modelcook) are added. A step of the recipe with the same name says more, and wins. A block written in code is not
visible here: Kiln.Blocks refuses one that brings files, and says to put it in blocks/*.json.

Names, and so the GUIDs that follow from <mod>:<kind>:<name>, are what Kiln.Blocks expects (Npc/NpcCook.cs):
- skeleton: the block's "skeleton", else the file's name without its extension;
- clip: "<skeleton>.<animation>", with ".once" when it plays once (the engine's moving and idle states loop, every other state plays once), ".rm" with
  root motion;
- graph: the animation block's name, or "<animation>.<body>" for a body on the same skeleton with slots: slots are the graph's;
- model: the model file's path in the mod without the extension, as a worn look's (cook/worn.py in tools/modelcook).

An animation with "retarget" brings clips authored on a rig of their own and names the "skeleton" they play on, a body's: its clips step pairs the rigs'
joints (anicook/retarget.py) and the skeleton stays the body's.
"""
import json
import os
import re
from pathlib import Path

ENGINE_FIELDS = {"idle": "Idle", "walk": "Walk", "run": "Run", "sprint": "Sprint", "idleCombat": "IdleCombat", "fall": "Fall", "idleSneak": "IdleSneak",
                 "sneak": "Sneak", "dead": "Dead", "stun": "Stun"}
LOOPED = {"Idle", "Walk", "Run", "Sprint", "IdleCombat", "IdleSneak", "Sneak"}
DERIVED = ("IdleCombat", "Fall")  # states a graph gets from Idle when the block leaves them out (and Stagger, Stun, Dead: see plan())
SECTIONS = ("animations", "bodies")
INLINE = ("creatures", "npcs")


class BlocksError(Exception):
    pass


def _relaxed(text):
    """JSON with // comments and trailing commas, as the block loader reads it."""
    out, i, n, in_str = [], 0, len(text), False
    while i < n:
        c = text[i]
        if in_str:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1])
                i += 2
                continue
            if c == '"':
                in_str = False
            i += 1
            continue
        if c == '"':
            in_str = True
            out.append(c)
        elif text.startswith("//", i):
            while i < n and text[i] != "\n":
                i += 1
            continue
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        else:
            out.append(c)
        i += 1
    return re.sub(r",(\s*[}\]])", r"\1", "".join(out))


def stem(path):
    """A file's name without its extension: the default skeleton name."""
    return Path(path).name.rsplit(".", 1)[0]


def model_name(path):
    """What a body's model GUID follows from: its path in the mod without the extension (as tools/modelcook's worn looks)."""
    p = Path(path).as_posix()
    return p[:p.rfind(".")] if "." in Path(p).name else p


def _slot(name):
    """A slot as the graph names it ("HandR"), however JSON cased it ("handR")."""
    from .graphgen import SLOTS
    return next((s for s in SLOTS if s.lower() == str(name).lower()), name)


def clip_spec(v, role=None):
    """A state's or layer's source as a dict: {"clip": name, ...} or {"blend": {...}}. A string is a glTF animation's name."""
    if isinstance(v, str):
        return {"clip": v}
    if isinstance(v, dict) and ("clip" in v or "blend" in v):
        return v
    raise BlocksError(f"{role or 'a state'}: a glTF animation's name, or {{\"clip\": ...}} or {{\"blend\": ...}}")


def clip_key(skeleton, animation, loop_default, c):
    """The clip's name in the cook, and its clips-step entry."""
    loop = c.get("loop", loop_default)
    key = f"{skeleton}.{animation}" + ("" if loop else ".once")
    entry = {"animation": animation, "loop": bool(loop)}
    if c.get("rootMotion"):
        key += ".rm"
        entry["rootMotion"] = True
    if c.get("events"):
        entry["events"] = c["events"]
    return key, entry


def read(mod_dir):
    """(file, name, kind, block) of every animation and body of the mod's blocks/*.json, inline ones of creatures and NPCs as "<name>.animation", "<name>.body"."""
    out = []
    for f in sorted((Path(mod_dir) / "blocks").glob("*.json")):
        try:
            doc = json.loads(_relaxed(f.read_text()))
        except ValueError:
            continue  # the block loader and kiln lint say what is wrong with it
        if not isinstance(doc, dict):
            continue
        for kind, section in (("animation", "animations"), ("body", "bodies")):
            for name, b in (doc.get(section) or {}).items():
                if isinstance(b, dict):
                    out.append((f.name, name, kind, b))
        for section in INLINE:
            for name, c in (doc.get(section) or {}).items():
                if not isinstance(c, dict):
                    continue
                for kind in ("animation", "body"):
                    if isinstance(c.get(kind), dict):
                        out.append((f.name, f"{name}.{kind}", kind, c[kind]))
    return out


def _in_mod(mod_dir, f):
    root = Path(mod_dir).resolve()
    p = (root / f).resolve()
    return p.is_relative_to(root) and p.is_file()


def plan(mod_dir):
    """What the blocks ask to be cooked: {"skeletons": {name: file}, "clips": {(skeleton, file): {key: entry}}, "graphs": [step], "models": [step]}.
    Paths are the mod's. Raises BlocksError for what can't be cooked as written."""
    blocks = read(mod_dir)
    skeletons, clips, graphs, models = {}, {}, [], []
    retargets, wanted = {}, []

    def skeleton_of(where, file, b):
        sk = b.get("skeleton") or stem(file)
        if skeletons.get(sk, file) != file:
            raise BlocksError(f"{where}: skeleton {sk!r} is {skeletons[sk]} already; name this one's \"skeleton\"")
        skeletons[sk] = file
        return sk

    bodies = []
    for fname, name, kind, b in blocks:
        if kind != "body" or not isinstance(b.get("model"), str):
            continue
        where = f"blocks/{fname}: body {name}"
        if not _in_mod(mod_dir, b["model"]):
            continue  # Kiln.Blocks names the missing file
        sk = skeleton_of(where, b["model"], b)
        models.append({"tool": "modelcook", "op": "model", "gltf": b["model"], "skeleton": sk, "name": model_name(b["model"]),
                       "stem": model_name(b["model"]).replace("/", "_"), "materials": "gltf", "mode": "new", "comment": f"{where}"})
        bodies.append((name, sk, {_slot(s): j for s, j in (b.get("slots") or {}).items()}))

    for fname, name, kind, b in blocks:
        if kind != "animation" or not isinstance(b.get("clips"), str):
            continue
        where = f"blocks/{fname}: animation {name}"
        if not _in_mod(mod_dir, b["clips"]):
            continue
        file = b["clips"]
        if b.get("retarget") is not None:
            # clips authored on a rig of their own, played on a skeleton a body (or another animation) of the mod makes
            if not b.get("skeleton"):
                raise BlocksError(f"{where}: retarget: say which skeleton the clips play on (\"skeleton\": a body's skeleton, its model file's name)")
            sk = b["skeleton"]
            cfg = b["retarget"] if isinstance(b["retarget"], dict) else {}
            if retargets.get((sk, file), cfg) != cfg:
                raise BlocksError(f"{where}: retarget: {file} is retargeted onto {sk} by another animation already, with other settings")
            retargets[(sk, file)] = cfg
            wanted.append((where, sk))
        else:
            sk = skeleton_of(where, file, b)
        own = clips.setdefault((sk, file), {})

        def source(role, v, loop_default):
            c = clip_spec(v, f"{where}, {role}")
            st = {}
            if "blend" in c:
                bl = c["blend"]
                keys = {}
                for at, anim in (bl.get("clips") or {}).items():
                    k, e = clip_key(sk, anim, loop_default, c)
                    own[k] = e
                    keys[str(at)] = k
                st["blend"] = {"input": bl.get("input", "moveDirection"), "clips": keys}
                for k2 in ("circular", "smoothing"):
                    if k2 in bl:
                        st["blend"][k2] = bl[k2]
            else:
                k, e = clip_key(sk, c["clip"], loop_default, c)
                if own.get(k, e) != e:
                    raise BlocksError(f"{where}, {role}: clip {c['clip']} is cooked twice with different events")
                own[k] = e
                st["clip"] = k
            for k2 in ("speed", "blendIn"):
                if k2 in c:
                    st[k2] = c[k2]
            if "loop" in c:
                st["loop"] = bool(c["loop"])
            return st, c

        states = {}
        for field, engine in ENGINE_FIELDS.items():
            if field in b and b[field] is not None:
                st, c = source(field, b[field], engine in LOOPED)
                st = dict({"engine": engine}, **st)
                if c.get("then"):
                    st["then"] = c["then"]
                states[engine] = st
        for sname, v in (b.get("states") or {}).items():
            if sname in ENGINE_FIELDS.values():
                raise BlocksError(f"{where}, states.{sname}: an engine state is a field of its own: \"{sname[0].lower() + sname[1:]}\"")
            st, c = source(f"states.{sname}", v, False)
            if c.get("then"):
                st["then"] = c["then"]
            states[sname] = st
        if "Idle" not in states:
            raise BlocksError(f"{where}: idle: every graph needs the engine's Idle")
        # the engine asks for these on any creature that stands in a fight or loses its footing: without its own clip, it stands as in Idle
        for engine in DERIVED:
            if engine not in states:
                states[engine] = dict(states["Idle"], engine=engine)
        # What the base creature's hit, stun and death sequences play becomes these (Kiln.Blocks makes those sequences play them). Stagger: the
        # block's, else its hit clip once over the whole body, else Idle once. Stun as Stagger; Dead: Idle, held.
        if "Stagger" in (b.get("states") or {}):
            raise BlocksError(f"{where}, states.Stagger: Stagger is a field of its own: \"stagger\"")
        idle = {k: v for k, v in states["Idle"].items() if k != "engine"}
        if b.get("stagger") is not None:
            st, _ = source("stagger", b["stagger"], False)
        elif b.get("hit") is not None:
            hc = clip_spec(b["hit"], f"{where}, hit")
            st, _ = source("hit", {k: hc[k] for k in ("clip", "blend", "speed") if k in hc}, False)
        else:
            st = dict(idle, loop=False)
        states["Stagger"] = st
        if "Stun" not in states:
            states["Stun"] = dict(st, engine="Stun")
        if "Dead" not in states:
            states["Dead"] = dict(idle, engine="Dead", loop=False)
        layers = []
        if b.get("hit") is not None:
            st, c = source("hit", b["hit"], False)
            lay = dict({"name": "hit", "overlay": "hit", "mode": "override" if not c.get("reference") else "additive"}, **st)
            if c.get("reference"):
                lay["reference"], _ = clip_key(sk, c["reference"], False, {})
                own[lay["reference"]] = {"animation": c["reference"], "loop": False}
            if c.get("bones"):
                lay["bones"] = c["bones"]
            layers.append(lay)
        for i, ly in enumerate(b.get("layers") or []):
            st, c = source(f"layers[{i}]", ly, False)
            lay = dict({"name": ly.get("name") or f"layer{i}", "mode": ly.get("mode") or ("additive" if ly.get("reference") else "override")}, **st)
            for k2 in ("overlay", "bones", "weight", "fadeIn", "fadeOut"):
                if ly.get(k2) is not None:
                    lay[k2] = ly[k2]
            if ly.get("reference"):
                lay["reference"], _ = clip_key(sk, ly["reference"], False, {})
                own[lay["reference"]] = {"animation": ly["reference"], "loop": False}
            layers.append(lay)
        base = {"tool": "animcook", "op": "graph", "name": name, "skeleton": sk, "states": states, "comment": where}
        if layers:
            base["layers"] = layers
        graphs.append(base)
        for bname, bsk, slots in bodies:
            if bsk == sk and slots:
                g = json.loads(json.dumps(base))
                g["name"] = f"{name}.{bname}"
                g["slots"] = slots
                g["comment"] = f"{where}, with the slots of body {bname}"
                graphs.append(g)
    for where, sk in wanted:
        if sk not in skeletons:
            raise BlocksError(f"{where}: retarget: no body or animation of the mod makes skeleton {sk!r} (has {', '.join(skeletons) or 'none'})")
    # a clip's GUID follows from <skeleton>.<animation>: two files can't give one skeleton two animations of one name
    seen = {}
    for (sk, file), entries in clips.items():
        for k in entries:
            if seen.setdefault(k, file) != file:
                raise BlocksError(f"clip {k}: animation {entries[k]['animation']!r} comes from {seen[k]} and {file}, on skeleton {sk}: rename one of them")
    return {"skeletons": skeletons, "clips": clips, "graphs": graphs, "models": models, "retargets": retargets}


def steps_from_blocks(mod_dir, recipe_dir):
    """The steps the mod's blocks ask for, paths relative to the recipe's folder, each with its "tool"."""
    try:
        p = plan(mod_dir)
    except BlocksError as e:
        raise BlocksError(f"blocks: {e}") from e
    rel = lambda f: os.path.relpath(Path(mod_dir) / f, recipe_dir)  # noqa: E731
    out = []
    for sk, file in p["skeletons"].items():
        out.append({"tool": "animcook", "op": "skeleton", "name": sk, "gltf": rel(file), "comment": "from blocks/*.json"})
    for (sk, file), entries in p["clips"].items():
        st = {"tool": "animcook", "op": "clips", "skeleton": sk, "gltf": rel(file), "clips": entries, "comment": "from blocks/*.json"}
        if (sk, file) in p["retargets"]:
            st["retarget"] = p["retargets"][(sk, file)]
        out.append(st)
    for g in p["graphs"]:
        out.append(g)
    for m in p["models"]:
        m = dict(m, gltf=rel(m["gltf"]))
        out.append(m)
    return out


def mod_dir_of(recipe_path):
    """The mod a recipe belongs to: the nearest folder above it with a kiln.mod.json, else the recipe's folder's parent, as <mod>/assets/cook.json is
    in a project whose build writes the manifest."""
    p = Path(recipe_path).resolve()
    for d in p.parents:
        if (d / "kiln.mod.json").exists():
            return d
    return p.parent.parent if (p.parent.parent / "blocks").is_dir() else None


def add_to(doc, recipe_path):
    """The recipe with the steps of the mod's blocks added; a step of the recipe of the same op and name (a skeleton, graph or model) wins."""
    mod = mod_dir_of(recipe_path)
    if mod is None or not (mod / "blocks").is_dir():
        return doc
    have = {(st.get("op"), st.get("name")) for st in doc.get("steps", [])}
    have |= {("clips", (st.get("skeleton"), st.get("gltf"))) for st in doc.get("steps", []) if st.get("op") == "clips"}
    extra = []
    for st in steps_from_blocks(mod, Path(recipe_path).resolve().parent):
        key = ("clips", (st["skeleton"], st["gltf"])) if st["op"] == "clips" else (st["op"], st["name"])
        if key not in have:
            extra.append(st)
    if extra:
        doc = dict(doc, steps=list(doc.get("steps", [])) + extra)
        doc["blocksSources"] = sorted(os.path.relpath(f, Path(recipe_path).resolve().parent) for f in (mod / "blocks").glob("*.json"))
    return doc
